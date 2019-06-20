﻿using System;
using System.Collections.Generic;
using Hangfire.Logging;
using System.Linq;
using System.Text;
using Hangfire.Annotations;
using Hangfire.Server;
using Hangfire.Storage.MySql.JobQueue;
using Hangfire.Storage.MySql.Locking;
using Hangfire.Storage.MySql.Monitoring;
using MySql.Data.MySqlClient;

namespace Hangfire.Storage.MySql
{
	public class MySqlStorage: JobStorage, IDisposable
	{
		private static readonly ILog Logger = LogProvider.GetLogger(typeof(MySqlStorage));

		private readonly string _connectionString;
		private readonly string _cachedToString;
		private readonly MySqlStorageOptions _options;

		internal PersistentJobQueueProviderCollection QueueProviders { get; private set; }

		public MySqlStorage(string connectionString, MySqlStorageOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_connectionString = FixConnectionString(
				connectionString ?? throw new ArgumentNullException(nameof(connectionString)));
			_cachedToString = BuildToString();

			if (options.PrepareSchemaIfNecessary)
			{
				using (var connection = CreateAndOpenConnection())
				{
					MySqlObjectsInstaller.Install(connection, options.TablesPrefix);
				}
			}

			InitializeQueueProviders();
		}

		private static string FixConnectionString(string connectionString)
		{
			string AddPart(string key, string value, string cs) =>
				cs.ToLower().Contains(key.ToLower()) ? cs : $"{cs};{key}={value}";

			return connectionString
				.PipeTo(x => AddPart("Allow User Variables", "true", x));
		}

		private void InitializeQueueProviders()
		{
			QueueProviders =
				new PersistentJobQueueProviderCollection(
					new MySqlJobQueueProvider(this, _options));
		}

		public override IEnumerable<IServerComponent> GetComponents()
		{
			yield return new ExpirationManager(this, _options);
			yield return new CountersAggregator(this, _options);
		}

		public override void WriteOptionsToLog(ILog logger)
		{
			logger.Info("Using the following options for SQL Server job storage:");
			logger.InfoFormat("    Queue poll interval: {0}.", _options.QueuePollInterval);
		}

		public override IMonitoringApi GetMonitoringApi() =>
			new MySqlMonitoringApi(this, _options);

		public override IStorageConnection GetConnection() =>
			new MySqlStorageConnection(this, _options);

		internal MySqlConnection CreateAndOpenConnection() =>
			new MySqlConnection(_connectionString).TapWith(c => c.Open());
		
		public void Dispose()
		{
			// There is no implementation
		}

		#region ToString

		public override string ToString() => _cachedToString;

		private static readonly string[] ServerKeys =
			{ "Data Source", "Server", "Address", "Addr", "Network Address" };

		private static readonly string[] DatabaseKeys =
			{ "Database", "Initial Catalog" };

		private string BuildToString()
		{
			var parts = _connectionString
				.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(x => x.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries))
				.Select(x => new { Key = x[0].Trim(), Value = x[1].Trim() })
				.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

			var serverName = ExtractPart(parts, ServerKeys) ?? "<unknown>";
			var databaseName = ExtractPart(parts, DatabaseKeys) ?? "<unknown>";

			return $"{GetType().GetFriendlyName()}({databaseName}@{serverName})";
		}

		private static string ExtractPart(
			IDictionary<string, string> parts, string[] aliases) =>
			aliases.Select(k => parts.TryGetOrDefault(k)).FirstOrDefault(v => v != null);

		#endregion
	}
}
