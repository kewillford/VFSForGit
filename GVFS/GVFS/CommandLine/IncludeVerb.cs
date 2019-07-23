﻿using CommandLine;
using GVFS.Common;
using GVFS.Common.Database;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GVFS.CommandLine
{
    [Verb(
        IncludeVerb.IncludeVerbName,
        HelpText = @"List, add, or remove from the list of folders that are included in VFS for Git's projection.
Folders need to be relative to the repos root directory.")
    ]
    public class IncludeVerb : GVFSVerb.ForExistingEnlistment
    {
        private const string IncludeVerbName = "include";
        private const string FolderListSeparator = ";";

        [Option(
            'a',
            "add",
            Required = false,
            Default = "",
            HelpText = "A semicolon-delimited list of repo root relative folders to include in the projection. Wildcards are not supported.")]
        public string Add { get; set; }

        [Option(
            'r',
            "remove",
            Required = false,
            Default = "",
            HelpText = "A semicolon-delimited list of repo root relative folders to remove from the projection. Wildcards are not supported.")]
        public string Remove { get; set; }

        [Option(
            'l',
            "list",
            Required = false,
            Default = false,
            HelpText = "List of folders included in the projection.")]
        public bool List { get; set; }

        protected override string VerbName => IncludeVerbName;

        protected override void Execute(GVFSEnlistment enlistment)
        {
            using (JsonTracer tracer = new JsonTracer(GVFSConstants.GVFSEtwProviderName, "Include"))
            {
                try
                {
                    tracer.AddLogFileEventListener(
                        GVFSEnlistment.GetNewGVFSLogFileName(enlistment.GVFSLogsRoot, GVFSConstants.LogFileTypes.Include),
                        EventLevel.Informational,
                        Keywords.Any);

                    bool needToChangeProjection = false;
                    using (GVFSDatabase database = new GVFSDatabase(new PhysicalFileSystem(), enlistment.EnlistmentRoot, new SqliteDatabase()))
                    {
                        IncludedFolderTable includedFolderTable = new IncludedFolderTable(database);
                        HashSet<string> directories = includedFolderTable.GetAll();

                        string[] foldersToRemove = this.ParseFolderList(this.Remove);
                        string[] foldersToAdd = this.ParseFolderList(this.Add);

                        if (this.List || (foldersToAdd.Length == 0 && foldersToRemove.Length == 0))
                        {
                            if (directories.Count == 0)
                            {
                                this.Output.WriteLine("No folders in included list.");
                            }
                            else
                            {
                                foreach (string directory in directories)
                                {
                                    this.Output.WriteLine(directory);
                                }
                            }

                            return;
                        }

                        foreach (string folder in foldersToRemove)
                        {
                            if (directories.Contains(folder))
                            {
                                needToChangeProjection = true;
                                break;
                            }
                        }

                        if (!needToChangeProjection)
                        {
                            foreach (string folder in foldersToAdd)
                            {
                                if (!directories.Contains(folder))
                                {
                                    needToChangeProjection = true;
                                    break;
                                }
                            }
                        }

                        if (needToChangeProjection)
                        {
                            // Make sure there is a clean git status before allowing inclusions to change
                            this.CheckGitStatus(tracer, enlistment);
                            if (!this.ShowStatusWhileRunning(
                                () =>
                                {
                                    foreach (string directoryPath in foldersToRemove)
                                    {
                                        tracer.RelatedInfo($"Removing '{directoryPath}' from included folders.");
                                        includedFolderTable.Remove(directoryPath);
                                    }

                                    foreach (string directoryPath in foldersToAdd)
                                    {
                                        tracer.RelatedInfo($"Adding '{directoryPath}' to included folders.");
                                        includedFolderTable.Add(directoryPath);
                                    }

                                    return true;
                                },
                                "Updating included folder set",
                                suppressGvfsLogMessage: true))
                            {
                                this.ReportErrorAndExit(tracer, "Failed to update included folder set.");
                            }
                        }
                    }

                    if (needToChangeProjection)
                    {
                        // Force a projection update to get the current inclusion set
                        this.ForceProjectionChange(tracer, enlistment);
                        tracer.RelatedInfo("Projection updated after adding or removing folders.");
                    }
                    else
                    {
                        this.WriteMessage(tracer, "No folders to update in included set.");
                    }
                }
                catch (Exception e)
                {
                    this.ReportErrorAndExit(tracer, e.ToString());
                }
            }
        }

        private string[] ParseFolderList(string folders)
        {
            if (string.IsNullOrEmpty(folders))
            {
                return new string[0];
            }
            else
            {
                return folders.Split(new[] { FolderListSeparator }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private void ForceProjectionChange(ITracer tracer, GVFSEnlistment enlistment)
        {
            string errorMessage = null;

            if (!this.ShowStatusWhileRunning(
                () =>
                {
                    GitProcess git = new GitProcess(enlistment);
                    GitProcess.Result checkoutResult = git.ForceCheckout("HEAD");

                    errorMessage = checkoutResult.Errors;
                    return checkoutResult.ExitCodeIsSuccess;
                },
                "Forcing a projection change",
                suppressGvfsLogMessage: true))
            {
                this.WriteMessage(tracer, "Failed to change projection: " + errorMessage);
            }
        }

        private void CheckGitStatus(ITracer tracer, GVFSEnlistment enlistment)
        {
            GitProcess.Result statusResult = null;
            if (!this.ShowStatusWhileRunning(
                () =>
                {
                    GitProcess git = new GitProcess(enlistment);
                    statusResult = git.Status(allowObjectDownloads: false, useStatusCache: false, showUntracked: true);
                    if (statusResult.ExitCodeIsFailure)
                    {
                        return false;
                    }

                    if (!statusResult.Output.Contains("nothing to commit, working tree clean"))
                    {
                        return false;
                    }

                    return true;
                },
                "Running git status",
                suppressGvfsLogMessage: true))
            {
                this.Output.WriteLine();

                if (statusResult.ExitCodeIsFailure)
                {
                    this.WriteMessage(tracer, "Failed to run git status: " + statusResult.Errors);
                }
                else
                {
                    this.WriteMessage(tracer, statusResult.Output);
                    this.WriteMessage(tracer, "git status reported that you have dirty files");
                    this.WriteMessage(tracer, "Either commit your changes or reset and clean");
                }

                this.ReportErrorAndExit(tracer, "Include was aborted");
            }
        }

        private void WriteMessage(ITracer tracer, string message)
        {
            this.Output.WriteLine(message);
            tracer.RelatedEvent(
                EventLevel.Informational,
                IncludeVerbName,
                new EventMetadata
                {
                    { TracingConstants.MessageKey.InfoMessage, message }
                });
        }
    }
}
