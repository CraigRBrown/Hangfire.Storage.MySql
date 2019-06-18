﻿using System;
using System.Data.Common;
using System.Threading;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage.MySql.Locking;
using MySql.Data.MySqlClient;

namespace Hangfire.Storage.MySql
{
	internal class CountersAggregator: IServerComponent
	{
		private static readonly ILog Logger = LogProvider.GetLogger(typeof(CountersAggregator));

		private static readonly TimeSpan PassInterval = TimeSpan.FromMilliseconds(500);
		private const int PassSize = 1000;

		private const string LockName = "CountersAggregator";
		private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

		private readonly MySqlStorage _storage;
		private readonly MySqlStorageOptions _options;
		private readonly string _aggregationQuery;

		public CountersAggregator(MySqlStorage storage, MySqlStorageOptions options)
		{
			_storage = storage ?? throw new ArgumentNullException(nameof(storage));
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_aggregationQuery = AggregationQuery(_options.TablesPrefix);
		}

		public void Execute(CancellationToken cancellationToken)
		{
			var prefix = _options.TablesPrefix;

			Logger.DebugFormat($"Aggregating records in '{prefix}Counter' table...");

			while (true)
			{
				var removedCount = AggregateCounter(cancellationToken);

				if (removedCount < PassSize)
					break;

				cancellationToken.WaitHandle.WaitOne(PassInterval);
				cancellationToken.ThrowIfCancellationRequested();
			}

			cancellationToken.WaitHandle.WaitOne(_options.CountersAggregateInterval);
		}

		private int AggregateCounter(CancellationToken token)
		{
			var prefix = _options.TablesPrefix;

			using (var connection = _storage.CreateAndOpenConnection())
			using (AcquireGlobalLock(token, connection, prefix))
			{
				const int count = PassSize;

				return Repeater
					.Create(connection, prefix)
					.Lock(LockableResource.Counter)
					.Wait(token)
					.Log(Logger)
					.Execute(_aggregationQuery, new { count });
			}
		}

		private static IDisposable AcquireGlobalLock(
			CancellationToken token, DbConnection connection, string prefix) =>
			_ResourceLock.AcquireAny(connection, prefix, LockTimeout, token, LockName);

		private static string AggregationQuery(string prefix) =>
			$@"/* CountersAggregator.AggregateCounters */
                drop table if exists __refs__;

                create temporary table __refs__ engine = memory as
                    select `Id` from `{prefix}Counter` limit @count;

                insert into `{prefix}AggregatedCounter` (`Key`, Value, ExpireAt)
                    select `Key`, SumValue, MaxExpireAt
                    from (
                        select `Key`, sum(Value) as SumValue, max(ExpireAt) AS MaxExpireAt
                        from `{prefix}Counter` c join __refs__ r on (r.Id = c.Id)
                        group by `Key`
                    ) _
                    on duplicate key update
                        Value = Value + values(Value),
                        ExpireAt = greatest(ExpireAt, values(ExpireAt));

                delete c from `{prefix}Counter` c join __refs__ r on (r.Id = c.Id);

                drop table __refs__;
            ";

		public override string ToString() => GetType().GetFriendlyName();
	}
}
