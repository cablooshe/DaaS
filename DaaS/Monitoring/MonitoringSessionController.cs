﻿// -----------------------------------------------------------------------
// <copyright file="MonitoringSessionController.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DaaS.Configuration;
using DaaS.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DaaS
{
    public class MonitoringSessionController
    {
        const string MonitoringFolder = "Monitoring";

        const int MinCpuThreshold = 50;
        const int MaxCustomActions = 20;
        const int MinMonitorDurationInSeconds = 5;
        const int MinThresholdDurationInSeconds = 15;
        const int MaxIntervalDays = 30;
        readonly int MaxSessionDuration = (int)TimeSpan.FromDays(365).TotalHours;

        public readonly static string TempFilePath = Path.Combine(EnvironmentVariables.LocalTemp, "Monitoring", "Logs");

        public static string GetLogsFolderForSession(string sessionId)
        {
            string logsFolderPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Logs);
            string folderName = Path.Combine(logsFolderPath, sessionId);
            FileSystemHelpers.CreateDirectoryIfNotExists(folderName);
            return folderName;
        }

        public static string GetCpuMonitoringPath(string folderName = "", bool relativePath = false)
        {
            string path;
            if (relativePath)
            {
                path = Path.Combine(@"data\DaaS", MonitoringFolder, folderName);
                path.ConvertBackSlashesToForwardSlashes();
            }
            else
            {
                path = Path.Combine(Settings.Instance.UserSiteStorageDirectory, MonitoringFolder, folderName);
                FileSystemHelpers.CreateDirectoryIfNotExistsSafe(path);
            }

            return path;
        }

        public MonitoringSession CreateSession(MonitoringSession monitoringSession)
        {
            string cpuMonitoringActive = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            var existingFiles = FileSystemHelpers.GetFilesInDirectory(cpuMonitoringActive, "*.json", false, SearchOption.TopDirectoryOnly);

            if (existingFiles.Count > 0)
            {
                throw new ApplicationException("Another monitoring session is already in progress");
            }
            else
            {
                ValidateSessionParameters(monitoringSession);
                FileSystemHelpers.DeleteDirectoryContentsSafe(cpuMonitoringActive);
                monitoringSession.StartDate = DateTime.UtcNow;
                monitoringSession.EndDate = DateTime.MinValue.ToUniversalTime();
                monitoringSession.SessionId = monitoringSession.StartDate.ToString(Constants.SessionFileNameFormat);
                monitoringSession.BlobStorageHostName = BlobController.GetBlobStorageHostName();
                monitoringSession.DefaultHostName = Settings.Instance.DefaultHostName;
                cpuMonitoringActive = Path.Combine(cpuMonitoringActive, monitoringSession.SessionId + ".json");

                if (monitoringSession.RuleType == RuleType.AlwaysOn
                    && monitoringSession.Mode == SessionMode.CollectKillAndAnalyze)
                {
                    monitoringSession.AnalysisStatus = AnalysisStatus.Continuous;
                }

                monitoringSession.SaveToDisk(cpuMonitoringActive);
                Logger.LogNewCpuMonitoringSession(monitoringSession);
            }

            return monitoringSession;
        }

        private void ValidateSessionParameters(MonitoringSession monitoringSession)
        {
            if (monitoringSession.CpuThreshold < MinCpuThreshold)
            {
                throw new InvalidOperationException($"CpuThreshold cannot be less than {MinCpuThreshold} percent");
            }

            if (monitoringSession.MaxActions > MaxCustomActions)
            {
                throw new InvalidOperationException($"MaxActions cannot be more than {MaxCustomActions} actions");
            }

            if (monitoringSession.MaximumNumberOfHours > MaxSessionDuration)
            {
                throw new InvalidOperationException($"MaximumNumberOfHours cannot be more than {MaxSessionDuration} hours");
            }

            if (monitoringSession.MonitorDuration < MinMonitorDurationInSeconds)
            {
                throw new InvalidOperationException($"MonitorDuration cannot be less than {MinMonitorDurationInSeconds} seconds");
            }
            if (monitoringSession.ThresholdSeconds < MinThresholdDurationInSeconds)
            {
                throw new InvalidOperationException($"ThresholdSeconds cannot be less than {MinThresholdDurationInSeconds} seconds");
            }
            if (monitoringSession.RuleType == RuleType.AlwaysOn)
            {
                if (monitoringSession.ActionsInInterval > monitoringSession.MaxActions)
                {
                    throw new InvalidOperationException($"ActionsInInterval ({monitoringSession.ActionsInInterval}) cannot be more than MaxActions ({monitoringSession.MaxActions})");
                }
                if (monitoringSession.ActionsInInterval > MaxCustomActions)
                {
                    throw new InvalidOperationException($"ActionsInInterval cannot be more than {MaxCustomActions} actions");
                }

                if (monitoringSession.IntervalDays > MaxIntervalDays)
                {
                    throw new InvalidOperationException($"IntervalDays cannot be more than {MaxIntervalDays} days");
                }
            }
        }

        public MonitoringSession GetSession(string sessionId)
        {
            string cpuMonitoringCompleted = GetCpuMonitoringPath(MonitoringSessionDirectories.Completed);
            var sessionFilePath = Path.Combine(cpuMonitoringCompleted, sessionId + ".json");
            if (FileSystemHelpers.FileExists(sessionFilePath))
            {
                var monitoringSession = FileSystemHelpers.FromJsonFile<MonitoringSession>(sessionFilePath);
                return monitoringSession;
            }
            else
            {
                return null;
            }
        }

        public string AnalyzeSession(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                throw new InvalidOperationException("Session does not exist or is not yet completed");
            }

            foreach (var log in session.FilesCollected)
            {
                if (string.IsNullOrWhiteSpace(log.ReportFile) && !string.IsNullOrWhiteSpace(log.FileName))
                {
                    MonitoringAnalysisController.QueueAnalysisRequest(sessionId, log.FileName);
                }
            }
            session.AnalysisStatus = AnalysisStatus.InProgress;
            SaveSession(session);
            return "Analysis request submitted";
        }

        public void DeleteSession(string sessionId)
        {
            DeleteFilesFromBlob(sessionId);

            string cpuMonitoringCompleted = GetCpuMonitoringPath(MonitoringSessionDirectories.Completed);
            var sessionFilePath = Path.Combine(cpuMonitoringCompleted, sessionId + ".json");
            if (FileSystemHelpers.FileExists(sessionFilePath))
            {
                FileSystemHelpers.DeleteFileSafe(sessionFilePath);
            }

            string logsFolderPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Logs);
            string logsFolder = Path.Combine(logsFolderPath, sessionId);

            if (FileSystemHelpers.DirectoryExists(logsFolder))
            {
                FileSystemHelpers.DeleteDirectoryContentsSafe(logsFolder);
                FileSystemHelpers.DeleteDirectorySafe(logsFolder);
            }
            Logger.LogCpuMonitoringVerboseEvent("Deleted session", sessionId);
        }

        private void DeleteFilesFromBlob(string sessionId)
        {
            try
            {
                var session = GetSession(sessionId);
                var fileBlobLegacy = BlobController.GetBlobForFile(GetRelativePathForSession(sessionId));
                if (fileBlobLegacy != null)
                {
                    fileBlobLegacy.DeleteIfExists(DeleteSnapshotsOption.None);
                }

                var fileBlob = BlobController.GetBlobForFile(GetRelativePathForSession(session.DefaultHostName, sessionId));
                if (fileBlob != null)
                {
                    fileBlob.DeleteIfExists(DeleteSnapshotsOption.None);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCpuMonitoringErrorEvent("Failed while deleting files from blob", ex, sessionId);
            }
        }

        public void TerminateActiveMonitoringSession()
        {
            string cpuMonitorPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            var existingFiles = FileSystemHelpers.GetFilesInDirectory(cpuMonitorPath, "*.json", false, SearchOption.TopDirectoryOnly);
            if (existingFiles.Count > 0)
            {
                FileSystemHelpers.DeleteDirectoryContentsSafe(cpuMonitorPath, true);
            }
        }

        public bool StopMonitoringSession()
        {
            Logger.LogCpuMonitoringVerboseEvent($"Inside the StopMonitoringSession method of MonitoringSessionController", string.Empty);
            string cpuMonitoringActivePath = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            var existingFiles = FileSystemHelpers.GetFilesInDirectory(cpuMonitoringActivePath, "*.json", false, SearchOption.TopDirectoryOnly);

            if (existingFiles.Count > 0)
            {
                var monitoringSession = FileSystemHelpers.FromJsonFile<MonitoringSession>(existingFiles.FirstOrDefault());
                Logger.LogCpuMonitoringVerboseEvent($"Stopping an active session {existingFiles.FirstOrDefault()}", monitoringSession.SessionId);
                var canwriteToFileSystem = CheckAndWaitTillFileSystemWritable(monitoringSession.SessionId);
                if (!canwriteToFileSystem)
                {
                    return false;
                }

                try
                {
                    monitoringSession.EndDate = DateTime.UtcNow;
                    string cpuMonitorCompletedPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Completed);
                    cpuMonitorCompletedPath = Path.Combine(cpuMonitorCompletedPath, monitoringSession.SessionId + ".json");

                    if (!FileSystemHelpers.FileExists(cpuMonitorCompletedPath))
                    {
                        monitoringSession.FilesCollected = GetCollectedLogsForSession(monitoringSession);
                        Logger.LogCpuMonitoringVerboseEvent($"Found {monitoringSession.FilesCollected.Count} files collected by CPU monitoring", monitoringSession.SessionId);
                        SaveSession(monitoringSession);
                        MoveMonitoringLogsToSession(monitoringSession.SessionId);
                    }
                    else
                    {
                        // some other instance probably ended up writing the file
                        // lets hope that finishes and files get moved properly
                    }

                    //
                    // Now delete the Active Session File
                    //
                    try
                    {
                        FileSystemHelpers.DeleteFileSafe(existingFiles.FirstOrDefault(), false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogCpuMonitoringErrorEvent("Failed while deleting the Active session file", ex, monitoringSession.SessionId);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogCpuMonitoringErrorEvent("Failed while marking a session as Complete", ex, monitoringSession.SessionId);
                    return false;
                }
            }

            return true;
        }

        public List<MonitoringFile> GetCollectedLogsForSession(MonitoringSession session)
        {
            var filesCollected = new List<MonitoringFile>();
            string folderName = GetLogsFolderForSession(session.SessionId);
            var reports = FileSystemHelpers.GetFilesInDirectory(folderName, "*.mht", false, SearchOption.TopDirectoryOnly);
            string sessionId = session.SessionId;

            try
            {
                string directoryPath = GetRelativePathForSession(session.DefaultHostName, sessionId);
                UpdateFilesCollected(sessionId, filesCollected, reports, directoryPath);

                string directoryPathLegacy = GetRelativePathForSession(sessionId);
                UpdateFilesCollected(sessionId, filesCollected, reports, directoryPathLegacy);
            }
            catch (Exception ex)
            {
                Logger.LogCpuMonitoringErrorEvent("Failed while getting the list of logs collected for the session", ex, sessionId);
            }

            return filesCollected;
        }

        private void UpdateFilesCollected(string sessionId, List<MonitoringFile> filesCollected, List<string> reports, string directoryPath)
        {
            try
            {
                var dir = BlobController.GetBlobDirectory(directoryPath);
                if (dir == null)
                {
                    //
                    // The directoryPath does not exist on Blob
                    //

                    return;
                }

                foreach (
                    IListBlobItem item in
                        dir.ListBlobs(useFlatBlobListing: true))
                {
                    var relativePath = item.Uri.ToString().Replace(item.Container.Uri.ToString() + "/", "");
                    string fileName = item.Uri.Segments.Last();
                    var monitoringFile = new MonitoringFile(fileName, relativePath);
                    AddReportsToMonitoringFile(sessionId, monitoringFile, reports);
                    filesCollected.Add(monitoringFile);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCpuMonitoringErrorEvent($"Failed while getting the list of logs collected for the session from {directoryPath}", ex, sessionId);
            }
        }

        internal static string GetRelativePathForSession(string defaultHostName, string sessionId)
        {
            return Path.Combine(defaultHostName, "Monitoring", "Logs", sessionId);
        }

        internal static string GetRelativePathForSession(string sessionId)
        {
            return Path.Combine("Monitoring", "Logs", sessionId);
        }

        //
        // Method is used specifically for updating the Active Session details
        // for the AlwaysOnCpu rule type
        //

        private void AddReportsToMonitoringFile(string sessionId, MonitoringFile monitoringFile, List<string> reports)
        {
            string fileName = Path.GetFileNameWithoutExtension(monitoringFile.FileName);
            if (reports.Any())
            {
                var reportFile = reports.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).StartsWith(fileName));
                if (!string.IsNullOrWhiteSpace(reportFile))
                {
                    monitoringFile.ReportFile = Path.GetFileName(reportFile);
                    monitoringFile.ReportFileRelativePath = MonitoringFile.GetRelativePath(sessionId, Path.GetFileName(reportFile));
                }
            }
        }

        internal void AddReportToLog(string sessionId, string logfileName, string reportFilePath, List<string> errors, bool shouldUpdateSessionStatus = true)
        {
            var session = GetSession(sessionId);
            var lockFile = AcquireSessionLock(session);

            foreach (var log in session.FilesCollected)
            {
                if (log.FileName.Equals(logfileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(reportFilePath))
                    {
                        log.ReportFile = reportFilePath;
                        log.ReportFileRelativePath = MonitoringFile.GetRelativePath(sessionId, Path.GetFileName(reportFilePath));
                    }
                    if (errors != null && errors.Count > 0)
                    {
                        if (log.AnalysisErrors != null)
                        {
                            log.AnalysisErrors.Concat(errors);
                        }
                        else
                        {
                            log.AnalysisErrors = errors;
                        }
                    }
                    break;
                }
            }

            if (shouldUpdateSessionStatus)
            {
                bool sessionAnalysisPending = session.FilesCollected.Any(log => string.IsNullOrWhiteSpace(log.ReportFile) && (log.AnalysisErrors == null));
                if (!sessionAnalysisPending)
                {
                    session.AnalysisStatus = AnalysisStatus.Completed;
                }
            }
            SaveSession(session, lockFile);
        }

        private bool CheckAndWaitTillFileSystemWritable(string sessionId)
        {
            int maxWaitCount = 6, waitCount = 0;
            bool isFileSystemReadOnly = FileSystemHelpers.IsFileSystemReadOnly();
            if (isFileSystemReadOnly)
            {
                Logger.LogCpuMonitoringVerboseEvent("Waiting till filesystem is readonly", sessionId);
                while (isFileSystemReadOnly && (waitCount <= maxWaitCount))
                {
                    isFileSystemReadOnly = FileSystemHelpers.IsFileSystemReadOnly();
                    Thread.Sleep(10 * 1000);
                    ++waitCount;
                }

                if (waitCount >= maxWaitCount)
                {
                    Logger.LogCpuMonitoringVerboseEvent("FileSystem is still readonly so exiting...", sessionId);
                    return false;
                }
                Logger.LogCpuMonitoringVerboseEvent("FileSystem is no more readonly", sessionId);
            }
            return true;
        }

        private LockFile AcquireSessionLock(MonitoringSession session, string methodName = "")
        {
            string sessionFilePath = (session.EndDate != DateTime.MinValue.ToUniversalTime()) ? GetCpuMonitoringPath(MonitoringSessionDirectories.Completed) : GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            string lockFilePath = sessionFilePath + ".lock";

            LockFile _lockFile = new LockFile(lockFilePath);
            int loopCount = 0;
            int lognum = 1;
            int maximumWaitTimeInSeconds = 15 * 60;

            while (!_lockFile.Lock($"AcquireSessionLock by {methodName} on {Environment.MachineName}") && loopCount <= maximumWaitTimeInSeconds)
            {
                ++loopCount;
                if (loopCount > lognum * 120)
                {
                    ++lognum;
                    Logger.LogCpuMonitoringVerboseEvent($"Waiting to acquire the lock on session file , loop {lognum}", session.SessionId);
                }
                Thread.Sleep(1000);
            }
            if (loopCount == maximumWaitTimeInSeconds)
            {
                Logger.LogCpuMonitoringVerboseEvent($"Deleting the lock file as it seems to be in an orphaned stage", session.SessionId);
                _lockFile.Release();
                return null;
            }
            return _lockFile;
        }

        public void SaveSession(MonitoringSession session, LockFile lockFile = null)
        {
            if (lockFile == null)
            {
                lockFile = AcquireSessionLock(session);
            }

            string cpuMonitorCompletedPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Completed);
            cpuMonitorCompletedPath = Path.Combine(cpuMonitorCompletedPath, session.SessionId + ".json");
            session.SaveToDisk(cpuMonitorCompletedPath);
            if (lockFile != null)
            {
                lockFile.Release();
            }
        }

        private void MoveMonitoringLogsToSession(string sessionId)
        {
            try
            {
                string logsFolderPath = GetLogsFolderForSession(sessionId);
                string monitoringFolderActive = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
                var filesCollected = FileSystemHelpers.GetFilesInDirectory(monitoringFolderActive, "*.log", false, SearchOption.TopDirectoryOnly);
                foreach (string monitoringLog in filesCollected)
                {
                    string fileName = Path.GetFileName(monitoringLog);
                    fileName = Path.Combine(logsFolderPath, fileName);
                    Logger.LogCpuMonitoringVerboseEvent($"Moving {monitoringLog} to {fileName}", sessionId);
                    RetryHelper.RetryOnException("Moving monitoring log to logs folder...", () =>
                    {
                        FileSystemHelpers.MoveFile(monitoringLog, fileName);
                    }, TimeSpan.FromSeconds(5), 5);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCpuMonitoringErrorEvent("Failed while moving monitoring logs for the session", ex, sessionId);
            }
        }

        public List<MonitoringSession> GetAllCompletedSessions()
        {
            var sessions = new List<MonitoringSession>();
            string cpuMonitorPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Completed);

            try
            {
                var existingSessions = FileSystemHelpers.GetFilesInDirectory(cpuMonitorPath, "*.json", false, SearchOption.TopDirectoryOnly);
                foreach (var session in existingSessions)
                {
                    var monitoringSession = FileSystemHelpers.FromJsonFile<MonitoringSession>(session);
                    sessions.Add(monitoringSession);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCpuMonitoringErrorEvent("Failed to get completed monitoring sessions", ex, string.Empty);
            }

            return sessions;
        }

        public MonitoringSession GetActiveSession()
        {
            string cpuMonitorPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            var existingFiles = FileSystemHelpers.GetFilesInDirectory(cpuMonitorPath, "*.json", false, SearchOption.TopDirectoryOnly);

            if (existingFiles.Count > 0)
            {
                Logger.LogDiagnostic($"Found an active monitoring session {existingFiles.FirstOrDefault()}");
                var session = FileSystemHelpers.FromJsonFile<MonitoringSession>(existingFiles.FirstOrDefault());
                return session;
            }
            else
            {
                Logger.LogDiagnostic($"Found no active monitoring session");
                return null;
            }
        }

        public IEnumerable<MonitoringLogsPerInstance> GetActiveSessionMonitoringLogs()
        {
            var logs = new List<MonitoringLogsPerInstance>();
            string cpuMonitorPath = GetCpuMonitoringPath(MonitoringSessionDirectories.Active);
            var activeInstances = HeartBeats.HeartBeatController.GetLiveInstances();

            if (GetActiveSession() == null)
            {
                return logs;
            }

            foreach (var logFile in FileSystemHelpers.GetFilesInDirectory(cpuMonitorPath, "*.log", false, SearchOption.TopDirectoryOnly))
            {
                string instanceName = Path.GetFileNameWithoutExtension(logFile);
                if (activeInstances.Any(x => x.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase)))
                {
                    string logContent = ReadEndTokens(logFile, 10, Encoding.Default, Environment.NewLine);
                    logs.Add(new MonitoringLogsPerInstance()
                    {
                        Instance = instanceName,
                        Logs = logContent
                    });
                }
            }

            return logs;
        }

        //
        // Get last 10 lines of very large text file > 10GB
        // https://stackoverflow.com/questions/398378/get-last-10-lines-of-very-large-text-file-10gb
        //
        private string ReadEndTokens(string path, long numberOfTokens, Encoding encoding, string tokenSeparator)
        {

            int sizeOfChar = encoding.GetByteCount("\n");
            byte[] buffer = encoding.GetBytes(tokenSeparator);


            using (FileStream fs = new FileStream(path, FileMode.Open))
            {
                long tokenCount = 0;
                long endPosition = fs.Length / sizeOfChar;

                for (long position = sizeOfChar; position < endPosition; position += sizeOfChar)
                {
                    fs.Seek(-position, SeekOrigin.End);
                    fs.Read(buffer, 0, buffer.Length);

                    if (encoding.GetString(buffer) == tokenSeparator)
                    {
                        tokenCount++;
                        if (tokenCount == numberOfTokens)
                        {
                            byte[] returnBuffer = new byte[fs.Length - fs.Position];
                            fs.Read(returnBuffer, 0, returnBuffer.Length);
                            return encoding.GetString(returnBuffer);
                        }
                    }
                }

                // handle case where number of tokens in file is less than numberOfTokens
                fs.Seek(0, SeekOrigin.Begin);
                buffer = new byte[fs.Length];
                fs.Read(buffer, 0, buffer.Length);
                return encoding.GetString(buffer);
            }
        }
    }
}
