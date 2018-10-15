﻿using System;
using System.Diagnostics;
using Raven.Server.Utils.Stats;

namespace Raven.Server.Documents.ETL.Stats
{
    public class EtlStatsAggregator : StatsAggregator<EtlRunStats, EtlStatsScope>
    {
        private volatile EtlPerformanceStats _performanceStats;

        public EtlStatsAggregator(int id, EtlStatsAggregator lastStats) : base(id, lastStats)
        {
        }

        public override EtlStatsScope CreateScope()
        {
            Debug.Assert(Scope == null);

            return Scope = new EtlStatsScope(Stats);
        }

        public EtlPerformanceStats ToPerformanceStats()
        {
            if (_performanceStats != null)
                return _performanceStats;

            lock (Stats)
            {
                if (_performanceStats != null)
                    return _performanceStats;

                return _performanceStats = CreatePerformanceStats(completed: true);
            }
        }

        private EtlPerformanceStats CreatePerformanceStats(bool completed)
        {
            return new EtlPerformanceStats(Scope.Duration)
            {
                Id = Id,
                Started = StartTime,
                Completed = completed ? StartTime.Add(Scope.Duration) : (DateTime?)null,
                Details = Scope.ToPerformanceOperation("ETL"),
                LastLoadedEtag = Stats.LastLoadedEtag,
                LastTransformedEtag = Stats.LastTransformedEtags,
                NumberOfExtractedItems = Stats.NumberOfExtractedItems,
                BatchCompleteReason = Stats.BatchCompleteReason
            };
        }
    }
}