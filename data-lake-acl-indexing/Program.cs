﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Microsoft.Extensions.Configuration;


namespace DataLakeACLIndexing
{
    class Program
    {
        const string DATA_LAKE_FILESYSTEM_NAME = "acldemo";
        const string SAMPLE_DATA_DIRECTORY = "SampleData";
        const string SEARCH_ACL_INDEX_NAME = "acltestindex";
        const string SEARCH_ACL_DATASOURCE_NAME = "acltestdatasource";
        const string SEARCH_ACL_INDEXER_NAME = "acltestindexer";

        async static Task Main(string[] args)
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            var settings = new AppSettings
            {
                SearchManagedIdentityID = configuration["searchManagedIdentityID"],
                SearchAdminKey = configuration["searchAdminKey"],
                SearchEndpoint = configuration["searchEndpoint"],
                DataLakeResourceID = configuration["dataLakeResourceID"],
                DataLakeEndpoint = configuration["dataLakeEndpoint"]
            };

            var credential = new DefaultAzureCredential();
            var dfsClient = new DataLakeServiceClient(new Uri(settings.DataLakeEndpoint), credential);

            var fileSystemClient = dfsClient.GetFileSystemClient(DATA_LAKE_FILESYSTEM_NAME);
            Console.WriteLine("Create {0} if not exists...", DATA_LAKE_FILESYSTEM_NAME);
            await fileSystemClient.CreateIfNotExistsAsync();

            var rootDirectoryClient = fileSystemClient.GetDirectoryClient(String.Empty);
            Console.WriteLine("Uploading sample data if not exists...");
            await UploadSampleDataIfNotExistsAsync(SAMPLE_DATA_DIRECTORY, rootDirectoryClient);

            Console.WriteLine("Applying ACLs to sample data...");
            await ApplyACLsToSampleData(rootDirectoryClient, settings);
 
            Console.WriteLine("Creating search index, data source, and indexer...");
            await CreateSearchResources(settings);
        }

        static async Task UploadSampleDataIfNotExistsAsync(string localDirectory, DataLakeDirectoryClient directoryClient)
        {
            foreach (string filePath in Directory.GetFiles(localDirectory))
            {
                string fileName = Path.GetFileName(filePath);
                DataLakeFileClient fileClient = directoryClient.GetFileClient(fileName);
                if (!await fileClient.ExistsAsync())
                {
                    await fileClient.UploadAsync(filePath);
                }
            }

            foreach (string directory in Directory.GetDirectories(localDirectory))
            {
                string directoryName = Path.GetFileNameWithoutExtension(directory);
                DataLakeDirectoryClient subDirectoryClient = directoryClient.GetSubDirectoryClient(directoryName);
                await subDirectoryClient.CreateIfNotExistsAsync();
                await UploadSampleDataIfNotExistsAsync(directory, subDirectoryClient);
            }
        }

        static async Task ApplyACLsToSampleData(DataLakeDirectoryClient rootDirectoryClient, AppSettings settings)
        {
            Console.WriteLine("Applying Execute and Read ACLs to root directory...");
            await ApplyACLsForDirectory(rootDirectoryClient, RolePermissions.Execute | RolePermissions.Read, settings);

            Console.WriteLine(@"Applying Execute and Read ACLs to root ""Files For Organization""...");
            var filesForOrganizationClient = rootDirectoryClient.GetFileClient("Files for Organization.txt");
            await ApplyACLsForFile(filesForOrganizationClient, RolePermissions.Execute | RolePermissions.Read, settings);

            Console.WriteLine("Applying Execute And Read ACLs to Shared Documents directory recursively...");
            var sharedDocumentsDirectoryClient = rootDirectoryClient.GetSubDirectoryClient("Shared Documents");
            await ApplyACLsForDirectory(sharedDocumentsDirectoryClient, RolePermissions.Execute | RolePermissions.Read,settings, recursive: true);

            Console.WriteLine("Applying Execute and Read ACLs to User Documents directory...");
            var userDocumentsDirectoryClient = rootDirectoryClient.GetSubDirectoryClient("User Documents");
            await ApplyACLsForDirectory(userDocumentsDirectoryClient, RolePermissions.Execute | RolePermissions.Read, settings);

            Console.WriteLine("Applying Execute and Read ACLs to Alice's document directory...");
            var aliceDirectoryClient = userDocumentsDirectoryClient.GetSubDirectoryClient("Alice");
            await ApplyACLsForDirectory(aliceDirectoryClient, RolePermissions.Execute | RolePermissions.Read, settings);

            Console.WriteLine(@"Applying Execute and Read ACLs to ""Alice.txt""...");
            var aliceTxtFile = aliceDirectoryClient.GetFileClient("alice.txt");
            await ApplyACLsForFile(aliceTxtFile, RolePermissions.Execute | RolePermissions.Read, settings);

            Console.WriteLine("Applying Execute and Read ACLs to John's document directory recursively...");
            var johnDirectoryClient = userDocumentsDirectoryClient.GetSubDirectoryClient("John");
            await ApplyACLsForDirectory(johnDirectoryClient, RolePermissions.Execute | RolePermissions.Read, settings, recursive: true);

            Console.WriteLine("Applying Execute and Read ACLs to Bob's document directory recursively...");
            var bobDirectoryClient = userDocumentsDirectoryClient.GetSubDirectoryClient("Bob");
            await ApplyACLsForDirectory(bobDirectoryClient, RolePermissions.Execute | RolePermissions.Read, settings, recursive: true);

            Console.WriteLine(@"Removing Execute and Read ACLs from ""c.txt""");
            var cClient = bobDirectoryClient.GetSubDirectoryClient("Reports").GetFileClient("c.txt");
            await RemoveACLsForFile(cClient, settings);

            Console.WriteLine(@"Removing Execute and Read ACLs from Bob's Sales directory recursively...");
            var salesClient = bobDirectoryClient.GetSubDirectoryClient("Sales");
            await RemoveACLsForDirectory(salesClient, settings, recursive: true);
        }

        static async Task ApplyACLsForDirectory(DataLakeDirectoryClient directoryClient, RolePermissions newACLs, AppSettings settings, bool recursive = false)
        {
            PathAccessControl directoryAccessControl =
                await directoryClient.GetAccessControlAsync();

            List<PathAccessControlItem> accessControlList = UpdateACLs(directoryAccessControl.AccessControlList, newACLs, settings);

            if (recursive)
            {
                await directoryClient.SetAccessControlRecursiveAsync(accessControlList);
            }
            else
            {
                await directoryClient.SetAccessControlListAsync(accessControlList);
            }
        }

        static async Task RemoveACLsForDirectory(DataLakeDirectoryClient directoryClient, AppSettings settings, bool recursive = false)
        {
            PathAccessControl directoryAccessControl =
                await directoryClient.GetAccessControlAsync();

            List<PathAccessControlItem> accessControlList = RemoveACLs(directoryAccessControl.AccessControlList, settings);

            if (recursive)
            {
                await directoryClient.SetAccessControlRecursiveAsync(accessControlList);
            }
            else
            {
                await directoryClient.SetAccessControlListAsync(accessControlList);
            }
        }

        static async Task ApplyACLsForFile(DataLakeFileClient fileClient, RolePermissions newACLs, AppSettings settings)
        {
            PathAccessControl fileAccessControl =
                await fileClient.GetAccessControlAsync();

            List<PathAccessControlItem> accessControlList = UpdateACLs(fileAccessControl.AccessControlList, newACLs, settings);

            await fileClient.SetAccessControlListAsync(accessControlList);
        }

        static async Task RemoveACLsForFile(DataLakeFileClient fileClient, AppSettings settings)
        {
            PathAccessControl fileAccessControl =
                await fileClient.GetAccessControlAsync();

            List<PathAccessControlItem> accessControlList = RemoveACLs(fileAccessControl.AccessControlList, settings);

            await fileClient.SetAccessControlListAsync(accessControlList);
        }

        static List<PathAccessControlItem> UpdateACLs(IEnumerable<PathAccessControlItem> existingACLs, RolePermissions newPermissionsForManagedIdentity, AppSettings settings)
        {
            List<PathAccessControlItem> accessControlList = existingACLs.ToList();
            PathAccessControlItem managedIdentityAcl = accessControlList.FirstOrDefault(
                accessControlItem => accessControlItem.AccessControlType == AccessControlType.User && accessControlItem.EntityId == settings.SearchManagedIdentityID);
            if (managedIdentityAcl == null)
            {
                managedIdentityAcl = new PathAccessControlItem(
                    accessControlType: AccessControlType.User,
                    permissions: RolePermissions.Execute | RolePermissions.Read,
                    entityId: settings.SearchManagedIdentityID);
                accessControlList.Add(managedIdentityAcl);
            }
            else
            {
                managedIdentityAcl.Permissions = RolePermissions.Execute | RolePermissions.Read;
            }

            return accessControlList;
        }

        static List<PathAccessControlItem> RemoveACLs(IEnumerable<PathAccessControlItem> existingACLs, AppSettings settings)
        {
            List<PathAccessControlItem> accessControlList = existingACLs.ToList();
            accessControlList.RemoveAll(
                accessControlItem => accessControlItem.AccessControlType == AccessControlType.User && accessControlItem.EntityId == settings.SearchManagedIdentityID);

            return accessControlList;
        }

        static async Task CreateSearchResources(AppSettings settings)
        {
            var searchCredential = new AzureKeyCredential(settings.SearchAdminKey);
            Uri searchEndpointUri = new Uri(settings.SearchEndpoint);
            SearchIndexClient indexClient = new SearchIndexClient(searchEndpointUri, searchCredential);

            Console.WriteLine("Deleting search index {0} if exists...", SEARCH_ACL_INDEX_NAME);
            try
            {
                await indexClient.GetIndexAsync(SEARCH_ACL_INDEX_NAME);
                await indexClient.DeleteIndexAsync(SEARCH_ACL_INDEX_NAME);
            }
            catch (RequestFailedException)
            {
                // Index didn't exist - continue
            }
    
            Console.WriteLine("Creating search index {0}...", SEARCH_ACL_INDEX_NAME);
            await indexClient.CreateOrUpdateIndexAsync(
                new SearchIndex(SEARCH_ACL_INDEX_NAME, fields: new[]
                {
                    new SearchField("key", SearchFieldDataType.String) { IsKey = true },
                    new SearchField("metadata_storage_path", SearchFieldDataType.String),
                    new SearchField("content", SearchFieldDataType.String)
                }));

            Console.WriteLine("Creating search data source {0}...", SEARCH_ACL_DATASOURCE_NAME);
            SearchIndexerClient indexerClient = new SearchIndexerClient(searchEndpointUri, searchCredential);
            await indexerClient.CreateOrUpdateDataSourceConnectionAsync(
                new SearchIndexerDataSourceConnection(
                    name: SEARCH_ACL_DATASOURCE_NAME,
                    type: SearchIndexerDataSourceType.AzureBlob,
                    connectionString: "ResourceId=" + settings.DataLakeResourceID,
                    container: new SearchIndexerDataContainer(name: DATA_LAKE_FILESYSTEM_NAME)));

            Console.WriteLine("Deleting search indexer {0} if exists...", SEARCH_ACL_INDEXER_NAME);
            try
            {
                await indexerClient.GetIndexerAsync(SEARCH_ACL_INDEXER_NAME);
                await indexerClient.DeleteIndexerAsync(SEARCH_ACL_INDEXER_NAME);
            }
            catch (RequestFailedException)
            {
                // Indexer didn't exist - continue
            }

            Console.WriteLine("Creating search indexer {0}...", SEARCH_ACL_INDEXER_NAME);
            await indexerClient.CreateIndexerAsync(
                new SearchIndexer(
                    name: SEARCH_ACL_INDEXER_NAME,
                    dataSourceName: SEARCH_ACL_DATASOURCE_NAME,
                    targetIndexName: SEARCH_ACL_INDEX_NAME)
                {
                    Parameters = new IndexingParameters
                    {
                        MaxFailedItems = -1,
                        IndexingParametersConfiguration = new IndexingParametersConfiguration
                        {
                            ParsingMode = BlobIndexerParsingMode.Text
                        }
                    }
                });
        }

        class AppSettings
        {
            public string SearchManagedIdentityID { get; set; }
            public string SearchAdminKey { get; set; }
            public string SearchEndpoint { get; set; }
            public string DataLakeEndpoint { get; set;}
            public string DataLakeResourceID { get; set; }
        }
    }
}
