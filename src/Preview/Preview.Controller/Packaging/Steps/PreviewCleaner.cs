﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using SenseNet.ContentRepository.Search.Indexing;
using SenseNet.ContentRepository.Storage;
using SenseNet.ContentRepository.Storage.Data;
using SenseNet.ContentRepository.Storage.Data.SqlClient;
using SenseNet.Diagnostics;
using SenseNet.Search;
using SenseNet.Search.Querying;
using Retrier = SenseNet.Tools.Retrier;

namespace SenseNet.Preview.Packaging.Steps
{
    public class PreviewCleaner
    {
        public event EventHandler OnFolderDeleted;
        public event EventHandler OnImageDeleted;

        private struct NodeInfo
        {
            public int Id;
            public string Path;
        }

        private static class Scripts
        {
            internal const string ContentTypeId = "SELECT PropertySetId FROM dbo.SchemaPropertySets WHERE Name = @Name";

            internal const string PreviewFoldersAllVersions = @"SELECT TOP {0} N.NodeId, N.Path FROM Nodes N
WHERE N.NodeTypeId = @NodeTypeId
	AND (N.Path like '%/Previews/V%' OR N.Name = 'Previews')
	{1}
ORDER BY NodeId";

            internal const string PreviewFoldersRoot = @"SELECT TOP {0} NodeId, Path FROM Nodes
WHERE NodeTypeId = @NodeTypeId
	AND (Name = 'Previews')
	{1}
ORDER BY NodeId";

            internal const string EmptyFolder = @" AND NOT EXISTS (select 0 from Nodes where ParentNodeId = N.NodeId)";

            internal const string PreviewImages = @"SELECT TOP {0} N.NodeId FROM Nodes N
WHERE N.NodeTypeId = @NodeTypeId
    {1}
ORDER BY N.NodeId";

            internal const string LastVersions = @"
                    SELECT N.NodeId, N.Path, V.VersionId, V.MajorNumber, V.MinorNumber, V.Status, 
	LastMajor = CASE 
		WHEN V.VersionId = N.LastMajorVersionId THEN CAST(1 AS BIT)
		ELSE CAST(0 AS BIT)
		END,
	LastMinor = CASE 
		WHEN V.VersionId = N.LastMinorVersionId THEN CAST(1 AS BIT)
		ELSE CAST(0 AS BIT)
		END
FROM Versions V join Nodes N on V.NodeId = N.NodeId
WHERE (N.Path = @Path) AND (V.VersionId = N.LastMajorVersionId OR V.VersionId = N.LastMinorVersionId)
ORDER BY MajorNumber, MinorNumber";
        }

        //========================================================================================= Properties

        private string Path { get; }
        private int MaxIndex { get; }
        private CleanupMode Mode { get; }

        private int MaxDegreeOfParallelism { get; }
        private int BlockSize { get; }

        private static readonly Lazy<SnTrace.SnTraceCategory> TraceCategory = new Lazy<SnTrace.SnTraceCategory>(() =>
        {
            var trace = SnTrace.Category("CleanupPreviews");
            trace.Enabled = true;
            return trace;
        });
        private static SnTrace.SnTraceCategory Trace => TraceCategory.Value;

        private MemoryCache _lastVersionCache;
        private static readonly int SystemFolderTypeId = GetTypeId("SystemFolder");
        private static readonly int PreviewImageTypeId = GetTypeId("PreviewImage");

        //========================================================================================= Constructors

        public PreviewCleaner(string path = null, CleanupMode cleanupMode = CleanupMode.AllVersions,
            int maxIndex = 0, int maxDegreeOfParallelism = 10, int blockSize = 500)
        {
            Path = path;
            MaxIndex = Math.Max(0, maxIndex);
            Mode = cleanupMode;

            MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
            BlockSize = Math.Max(1, blockSize);
        }

        //========================================================================================= Public API

        public void Execute()
        {
            _lastVersionCache = new MemoryCache("LastVersions");

            if (Mode != CleanupMode.EmptyFoldersOnly)
            {
                // Delete folders only if we have to delete:
                //      1. everything or
                //      2. old versions
                if (MaxIndex == 0 || Mode == CleanupMode.KeepLastVersions)
                    DeletePreviewFolders();

                // Keep the first few images, delete the rest one by one.
                // All unneeded folders (for old versions) are already deleted at this point.
                if (MaxIndex > 0)
                    DeletePreviewImages();
            }

            DeleteEmptyPreviewFolders();

            _lastVersionCache.Dispose();
        }

        //========================================================================================= Internal methods

        #region Delete preview images

        private void DeletePreviewImages()
        {
            var blockCount = 0;
            var lastDeletedId = 0;

            while (DeletePreviewImagesBlock(Path, MaxIndex, lastDeletedId, BlockSize, out lastDeletedId))
            {
                var message = $"Preview image delete block {blockCount++} finished.";
                Trace.Write(message);
            }
        }
        private bool DeletePreviewImagesBlock(string path, int maxIndex, int minimumNodeId, int blockSize, out int lastDeletedId)
        {
            lastDeletedId = 0;

            if (!LoadPreviewImagesBlock(path, maxIndex, minimumNodeId, blockSize, out var buffer))
                return false;

            var workerBlock = new ActionBlock<int>(
                async imageId =>
                {
                    await DeleteContentFromDb(imageId);
                    OnImageDeleted?.Invoke(imageId, EventArgs.Empty);
                },
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism });

            foreach (var image in buffer)
                workerBlock.Post(image.Id);

            lastDeletedId = buffer.Max(n => n.Id);

            workerBlock.Complete();
            workerBlock.Completion.Wait();

            return true;
        }
        private static bool LoadPreviewImagesBlock(string path, int maxIndex, int minimumNodeId, int blockSize, out List<NodeInfo> buffer)
        {
            var block = new List<NodeInfo>();
            Retrier.Retry(3, 1000, typeof(Exception), () => { block = LoadPreviewImagesBlockInternal(path, maxIndex, minimumNodeId, blockSize); });

            buffer = block;

            return block.Any();
        }
        private static List<NodeInfo> LoadPreviewImagesBlockInternal(string path, int maxIndex, int minimumNodeId, int blockSize)
        {
            var filters = new StringBuilder();
            var parameters = new List<IDataParameter>();

            if (!string.IsNullOrEmpty(path))
            {
                filters.AppendLine(" AND Path like @PathStart");
                parameters.Add(new SqlParameter("@PathStart", SqlDbType.NVarChar) { Value = path.TrimEnd('/') + "/%" });
            }

            if (maxIndex > 0)
            {
                filters.AppendLine(" AND N.[Index] > @MaxIndex");
                parameters.Add(new SqlParameter("@MaxIndex", SqlDbType.Int) { Value = maxIndex });
            }

            if (minimumNodeId > 0)
            {
                filters.AppendLine(" AND NodeId > @MinimumNodeId");
                parameters.Add(new SqlParameter("@MinimumNodeId", SqlDbType.Int) { Value = minimumNodeId });
            }

            parameters.Add(new SqlParameter("@NodeTypeId", SqlDbType.Int) { Value = PreviewImageTypeId });

            return LoadData(string.Format(Scripts.PreviewImages, blockSize, filters), parameters.ToArray());
        }

        #endregion

        #region Delete preview folders

        private void DeletePreviewFolders()
        {
            var blockCount = 0;
            var lastDeletedId = 0;
            var keepLastVersions = Mode == CleanupMode.KeepLastVersions;

            while (DeletePreviewFoldersBlock(Path, keepLastVersions, lastDeletedId, BlockSize, out lastDeletedId))
            {
                var message = $"Preview folder delete block {blockCount++} finished.";
                Trace.Write(message);
            }
        }
        private bool DeletePreviewFoldersBlock(string path, bool keepLastVersions, int minimumNodeId, int blockSize, out int lastDeletedId)
        {
            lastDeletedId = 0;

            if (!LoadPreviewFoldersBlock(path, keepLastVersions, minimumNodeId, blockSize, out var buffer))
                return false;

            var pathBag = new ConcurrentBag<string>();
            var workerBlock = new ActionBlock<NodeInfo>(
                async node =>
                {
                    if (!keepLastVersions || IsFolderDeletable(node.Path))
                    {
                        await DeleteContentFromDb(node.Id);
                        OnFolderDeleted?.Invoke(node.Id, EventArgs.Empty);

                        if (!string.IsNullOrEmpty(node.Path))
                            pathBag.Add(node.Path);
                    }
                },
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism });

            foreach (var folder in buffer)
            {
                workerBlock.Post(folder);
            }

            lastDeletedId = buffer.Max(n => n.Id);

            workerBlock.Complete();
            workerBlock.Completion.Wait();

            Trace.Write("Removing preview folder block from the index.");

            IndexManager.IndexingEngine.WriteIndex(pathBag.SelectMany(p => new[]
                {
                    new SnTerm(IndexFieldName.InTree, p),
                    new SnTerm(IndexFieldName.Path, p)
                }),
                null, null);

            return true;
        }
        private static bool LoadPreviewFoldersBlock(string path, bool keepLastVersions, int minimumNodeId, int blockSize, out List<NodeInfo> buffer)
        {
            var block = new List<NodeInfo>();
            Retrier.Retry(3, 1000, typeof(Exception), () => { block = LoadPreviewFoldersBlockInternal(path, keepLastVersions, minimumNodeId, blockSize); });

            buffer = block;

            return block.Any();
        }
        private static List<NodeInfo> LoadPreviewFoldersBlockInternal(string path, bool keepLastVersions, int minimumNodeId, int blockSize)
        {
            var filters = new StringBuilder();
            var parameters = new List<IDataParameter>();

            if (!string.IsNullOrEmpty(path))
            {
                filters.AppendLine(" AND Path like @PathStart");
                parameters.Add(new SqlParameter("@PathStart", SqlDbType.NVarChar) { Value = path.TrimEnd('/') + "/%" });
            }
            if (minimumNodeId > 0)
            {
                filters.AppendLine(" AND NodeId > @MinimumNodeId");
                parameters.Add(new SqlParameter("@MinimumNodeId", SqlDbType.Int) { Value = minimumNodeId });
            }

            parameters.Add(new SqlParameter("@NodeTypeId", SqlDbType.Int) { Value = SystemFolderTypeId });

            var script = keepLastVersions
                ? string.Format(Scripts.PreviewFoldersAllVersions, blockSize, filters)
                : string.Format(Scripts.PreviewFoldersRoot, blockSize, filters);

            return LoadData(script, parameters.ToArray());
        }

        #endregion

        #region Delete empty preview folders

        private void DeleteEmptyPreviewFolders()
        {
            var blockCount = 0;

            while (DeleteEmptyPreviewFoldersBlock(Path, BlockSize))
            {
                var message = $"Preview folder delete block {blockCount++} finished.";
                Trace.Write(message);
            }
        }
        private bool DeleteEmptyPreviewFoldersBlock(string path, int blockSize)
        {
            if (!LoadEmptyPreviewFoldersBlock(path, blockSize, out var buffer))
                return false;

            var pathBag = new ConcurrentBag<string>();
            var workerBlock = new ActionBlock<NodeInfo>(
                async node =>
                {
                    await DeleteContentFromDb(node.Id);
                    OnFolderDeleted?.Invoke(node.Id, EventArgs.Empty);

                    if (!string.IsNullOrEmpty(node.Path))
                        pathBag.Add(node.Path);
                },
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism });

            foreach (var folder in buffer)
            {
                workerBlock.Post(folder);
            }

            workerBlock.Complete();
            workerBlock.Completion.Wait();

            Trace.Write("Removing preview folder block from the index.");

            IndexManager.IndexingEngine.WriteIndex(pathBag.SelectMany(p => new[]
                {
                    new SnTerm(IndexFieldName.InTree, p),
                    new SnTerm(IndexFieldName.Path, p)
                }),
                null, null);

            return true;
        }
        private static bool LoadEmptyPreviewFoldersBlock(string path, int blockSize, out List<NodeInfo> buffer)
        {
            var block = new List<NodeInfo>();
            Retrier.Retry(3, 1000, typeof(Exception), () => { block = LoadEmptyPreviewFoldersBlockInternal(path, blockSize); });

            buffer = block;

            return block.Any();
        }
        private static List<NodeInfo> LoadEmptyPreviewFoldersBlockInternal(string path, int blockSize)
        {
            var filters = new StringBuilder(Scripts.EmptyFolder);
            var parameters = new List<IDataParameter>();

            if (!string.IsNullOrEmpty(path))
            {
                filters.AppendLine(" AND Path like @PathStart");
                parameters.Add(new SqlParameter("@PathStart", SqlDbType.NVarChar) { Value = path.TrimEnd('/') + "/%" });
            }

            parameters.Add(new SqlParameter("@NodeTypeId", SqlDbType.Int) { Value = SystemFolderTypeId });

            return LoadData(string.Format(Scripts.PreviewFoldersAllVersions, blockSize, filters), parameters.ToArray());
        }

        #endregion

        #region Database operations

        private static async Task DeleteContentFromDb(int nodeId)
        {
            await Retrier.RetryAsync(3, 3000, () => DeleteContentFromDbInternalAsync(nodeId), (counter, exc) =>
            {
                if (exc == null)
                    return true;

                Trace.WriteError($"Content {nodeId} could not be deleted. {exc.Message}");
                return false;
            });
        }
        private static async Task DeleteContentFromDbInternalAsync(int nodeId)
        {
            using (var proc = DataProvider.Instance.CreateDataProcedure("proc_Node_DeletePhysical")
                .AddParameter("@NodeId", nodeId)
                .AddParameter("@Timestamp", DBNull.Value, DbType.Int32))
            {
                proc.CommandType = CommandType.StoredProcedure;

                // assume this is a SQL procedure so we can execute it asynchronously
                await ((SqlProcedure)proc).ExecuteNonQueryAsync();

                Trace.Write($"Content {nodeId} DELETED from database.");
            }
        }

        private static int GetTypeId(string contentTypeName)
        {
            using (var proc = DataProvider.Instance.CreateDataProcedure(Scripts.ContentTypeId)
                .AddParameter("@Name", contentTypeName))
            {
                proc.CommandType = CommandType.Text;

                return (int)proc.ExecuteScalar();
            }
        }

        private static Tuple<VersionNumber, VersionNumber> GetLastVersions(string path)
        {
            VersionNumber majorVersion = null;
            VersionNumber minorVersion = null;

            SqlProcedure cmd = null;
            SqlDataReader reader = null;

            try
            {
                cmd = new SqlProcedure
                {
                    CommandText = Scripts.LastVersions,
                    CommandType = CommandType.Text
                };
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
                reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var majorNumber = reader.GetInt16(reader.GetOrdinal("MajorNumber"));
                    var minorNumber = reader.GetInt16(reader.GetOrdinal("MinorNumber"));
                    var statusCode = reader.GetInt16(reader.GetOrdinal("Status"));
                    var isLastMajor = reader.GetBoolean(reader.GetOrdinal("LastMajor"));
                    var isLastMinor = reader.GetBoolean(reader.GetOrdinal("LastMinor"));

                    var versionNumber = new VersionNumber(majorNumber, minorNumber, (VersionStatus)statusCode);

                    if (isLastMajor)
                        majorVersion = versionNumber;
                    if (isLastMinor)
                        minorVersion = versionNumber;
                }

            }
            finally
            {
                reader?.Dispose();
                cmd?.Dispose();
            }

            return new Tuple<VersionNumber, VersionNumber>(majorVersion, minorVersion);
        }

        private static List<NodeInfo> LoadData(string script, params IDataParameter[] parameters)
        {
            var buffer = new List<NodeInfo>();

            using (var proc = DataProvider.Instance.CreateDataProcedure(script))
            {
                proc.CommandType = CommandType.Text;

                if (parameters != null)
                {
                    foreach (var dataParameter in parameters)
                    {
                        proc.Parameters.Add(dataParameter);
                    }
                }

                using (var reader = proc.ExecuteReader())
                {
                    if (!reader.HasRows)
                        return buffer;

                    while (reader.Read())
                    {
                        buffer.Add(GetNodeInfo(reader));
                    }
                }

                return buffer;
            }
        }

        #endregion

        //========================================================================================= Helper methods

        private static NodeInfo GetNodeInfo(IDataReader reader)
        {
            var node = new NodeInfo { Id = reader.GetSafeInt32(0) };
            if (reader.FieldCount > 1)
                node.Path = reader.GetSafeString(1);

            return node;
        }
        private bool IsFolderDeletable(string path)
        {
            var name = path.Substring(path.LastIndexOf("/", StringComparison.InvariantCultureIgnoreCase) + 1);
            if (!name.StartsWith("V"))
                return false;

            if (!VersionNumber.TryParse(name, out var version))
                return false;

            // do not delete locked versions
            if (version.Status == VersionStatus.Locked)
                return false;

            VersionNumber lastMajor;
            VersionNumber lastMinor;

            // look up the content (most likely a file) that this preview folder is related to
            var contentPath = RepositoryPath.GetParentPath(RepositoryPath.GetParentPath(path));

            // get cached last versions for the content if possible
            if (!(_lastVersionCache.Get(contentPath) is VersionNumber[] versions))
            {
                // not in cache, load from database
                var versionNumbers = GetLastVersions(contentPath);
                lastMajor = versionNumbers.Item1;
                lastMinor = versionNumbers.Item2;
                versions = new[] { lastMajor, lastMinor };

                _lastVersionCache.Set(contentPath, versions, ObjectCache.InfiniteAbsoluteExpiration);
            }
            else
            {
                lastMajor = versions[0];
                lastMinor = versions[1];
            }

            return version != lastMajor && version != lastMinor;
        }
    }
}
