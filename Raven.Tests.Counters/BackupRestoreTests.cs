﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using Raven.Abstractions.Counters;
using Raven.Database;
using Raven.Database.Config;
using Raven.Database.Counters;
using Raven.Database.Counters.Backup;
using Raven.Database.Extensions;
using Raven.Database.Server;
using Raven.Tests.Helpers.Util;
using Xunit;
using Xunit.Sdk;

namespace Raven.Tests.Counters
{
	public class BackupRestoreTests : IDisposable
	{
		private readonly string BackupDestinationDirectory = "TestCounterBackup";
		private readonly string BackupSourceDirectory = "TestCounterData";
		private readonly string RestoreToDirectory = "TestCounterRestore";
		private readonly string DocumentDatabaseDirectory = "TestCounterDB";
		private const string CounterStorageId = "FooBar";

		private readonly CounterStorage storage;
		private readonly DocumentDatabase documentDatabase;
		private readonly RavenConfiguration config;

		public BackupRestoreTests()
		{
			DeleteTempFolders();

			var uniqueId = Guid.NewGuid();
			BackupDestinationDirectory += uniqueId;
			BackupSourceDirectory += uniqueId;
			RestoreToDirectory += uniqueId;
			DocumentDatabaseDirectory += uniqueId;

			config = new RavenConfiguration
			{
				CountersDataDirectory = BackupSourceDirectory,
				CountersDatabaseName = "TestCounterDB",				
				Port = 8090,
				DataDirectory = DocumentDatabaseDirectory,
				RunInMemory = false,
				DefaultStorageTypeName = "Voron",
				AnonymousUserAccessMode = AnonymousUserAccessMode.Admin, 
				Encryption = { UseFips = SettingsHelper.UseFipsEncryptionAlgorithms },
			};

			config.Settings["Raven/StorageTypeName"] = config.DefaultStorageTypeName;
			config.PostInit();

			storage = new CounterStorage("http://localhost:8080","TestCounter",config);
			storage.CounterStorageEnvironment.Options.IncrementalBackupEnabled = true;
			documentDatabase = new DocumentDatabase(config,null);
		}

		private static void DeleteTempFolders()
		{
			var directoriesToDelete = Directory.EnumerateDirectories(Directory.GetCurrentDirectory(), "TestCounter*", SearchOption.TopDirectoryOnly).ToList();
			directoriesToDelete.ForEach(dir =>
			{
				try
				{
					IOExtensions.DeleteDirectory(dir);
				}
				catch (IOException)
				{
				}
			});
			
		}

		public void Dispose()
		{			
			storage.Dispose();
			documentDatabase.Dispose();
		}

		[Fact]
		public void Full_backup_and_restore_should_work()
		{
			StoreCounterChange(5,storage);
			StoreCounterChange(-2, storage);
			StoreCounterChange(3, storage);

			var backupOperation = NewBackupOperation(false);
			backupOperation.Execute();

			var restoreOperation = NewRestoreOperation();
			restoreOperation.Execute();

			var restoreConfig = new RavenConfiguration
			{
				CountersDataDirectory = RestoreToDirectory,
				CountersDatabaseName = "TestCounterDB",
				RunInMemory = false
			};

			using (var restoredStorage = new CounterStorage("http://localhost:8081", "RestoredCounter", restoreConfig))
			{
				using (var reader = restoredStorage.CreateReader())
				{
					Assert.Equal(6,reader.GetCounterOverallTotal("Bar", "Foo"));
					var counter = reader.GetCounterValuesByPrefix("Bar", "Foo");
					var counterValues = counter.CounterValues.ToArray();

					Assert.Equal(8, counterValues[0].Value);
					Assert.True(counterValues[0].IsPositive);
					Assert.Equal(2, counterValues[1].Value);
					Assert.False(counterValues[1].IsPositive);
				}
			}
		}

		[Fact]
		public void Incremental_backup_and_restore_should_work()
		{
			var backupOperation = NewBackupOperation(true);

			StoreCounterChange(5, storage);
			backupOperation.Execute(); 
			Thread.Sleep(100);
			StoreCounterChange(-2, storage);
			backupOperation.Execute();
			Thread.Sleep(100);
			StoreCounterChange(3, storage);
			backupOperation.Execute();

			var restoreOperation = NewRestoreOperation();
			restoreOperation.Execute();

			var restoreConfig = new RavenConfiguration
			{
				CountersDataDirectory = RestoreToDirectory,
				CountersDatabaseName = "TestCounterDB",
				RunInMemory = false
			};

			using (var restoredStorage = new CounterStorage("http://localhost:8081", "RestoredCounter", restoreConfig))
			{
				using (var reader = restoredStorage.CreateReader())
				{
					Assert.Equal(6, reader.GetCounterOverallTotal("Bar", "Foo"));

					var counter = reader.GetCounterValuesByPrefix("Bar", "Foo");
					var counterValues = counter.CounterValues.ToArray();

					Assert.Equal(8, counterValues[0].Value);
					Assert.True(counterValues[0].IsPositive);
					Assert.Equal(2, counterValues[1].Value);
					Assert.False(counterValues[1].IsPositive);
				}
			}
		}

		private void StoreCounterChange(long change, CounterStorage counterStorage)
		{
			using (var writer = counterStorage.CreateWriter())
			{
				writer.Store("Bar", "Foo", change);
				writer.Commit();
			}
		}

		protected BackupOperation NewBackupOperation(bool isIncremental)
		{
			return new BackupOperation(documentDatabase,
				config.CountersDataDirectory,
				BackupDestinationDirectory,
				storage.CounterStorageEnvironment,
				isIncremental,
				new CounterStorageDocument
				{
					Id = CounterStorageId
				});
		}

		protected RestoreOperation NewRestoreOperation()
		{			
			return new RestoreOperation(new CounterRestoreRequest
			{
				BackupLocation = BackupDestinationDirectory,
				Id = CounterStorageId,
				RestoreToLocation = RestoreToDirectory
			}, obj => { });
		}
	}
}
