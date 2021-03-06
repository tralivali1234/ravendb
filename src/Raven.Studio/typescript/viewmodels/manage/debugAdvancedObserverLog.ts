import viewModelBase = require("viewmodels/viewModelBase");

import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import columnsSelector = require("viewmodels/partial/columnsSelector");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import columnPreviewPlugin = require("widgets/virtualGrid/columnPreviewPlugin");
import generalUtils = require("common/generalUtils");

import getClusterObserverDecisionsCommand = require("commands/database/cluster/getClusterObserverDecisionsCommand");
import toggleClusterObserverCommand = require("commands/database/cluster/toggleClusterObserverCommand");
import eventsCollector = require("common/eventsCollector");
import clusterTopologyManager = require("common/shell/clusterTopologyManager");

class clusterObserverLog extends viewModelBase {

    decisions = ko.observable<Raven.Server.ServerWide.Maintenance.ClusterObserverDecisions>();
    topology = clusterTopologyManager.default.topology;
    observerSuspended = ko.observable<boolean>();
    noLeader = ko.observable<boolean>(false);

    private gridController = ko.observable<virtualGridController<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>>();
    columnsSelector = new columnsSelector<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>();
    private columnPreview = new columnPreviewPlugin<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>();

    termChanged: KnockoutComputed<boolean>;

    spinners = {
        refresh: ko.observable<boolean>(false),
        toggleObserver: ko.observable<boolean>(false)
    };

    constructor() {
        super();

        this.termChanged = ko.pureComputed(() => {
            const topologyTerm = this.topology().currentTerm();
            const dataTerm = this.decisions().Term;
            const hasLeader = !this.noLeader();

            return hasLeader && topologyTerm !== dataTerm;
        })
    }

    activate(args: any) {
        super.activate(args);

        return this.loadDecisions();
    }

    compositionComplete(): void {
        super.compositionComplete();

        const fetcher = () => {
            const log = this.decisions();
            if (!log) {
                return $.when({
                    totalResultCount: 0,
                    items: []
                } as pagedResult<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>);
            }
            return $.when({
                totalResultCount: log.ObserverLog.length,
                items: log.ObserverLog
            } as pagedResult<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>);
        };

        const grid = this.gridController();
        grid.headerVisible(true);
        grid.init(fetcher, () =>
            [
                new textColumn<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>(grid, x => generalUtils.formatUtcDateAsLocal(x.Date), "Date", "20%"),
                new textColumn<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>(grid, x => x.Database, "Database", "20%"),
                new textColumn<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>(grid, x => x.Message, "Message", "60%")
            ]
        );

        this.columnPreview.install("virtual-grid", ".js-observer-log-tooltip", 
            (entry: Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry, 
             column: textColumn<Raven.Server.ServerWide.Maintenance.ClusterObserverLogEntry>, e: JQueryEventObject, 
             onValue: (context: any, valueToCopy?: string) => void) => {
            const value = column.getCellValue(entry);
            if (column.header === "Date") {
                onValue(moment.utc(entry.Date), entry.Date);
            } else if (!_.isUndefined(value)) {
                onValue(value);
            }
        });
    }

    private loadDecisions() {
        const loadTask = $.Deferred<void>();
        
        new getClusterObserverDecisionsCommand()
            .execute()
            .done(response => {
                response.ObserverLog.reverse();
                this.decisions(response);
                this.observerSuspended(response.Suspended);
                this.noLeader(false);
                
                loadTask.resolve();
            })
            .fail((response: JQueryXHR) => {
                if (response && response.responseJSON ) {
                    const type = response.responseJSON['Type'];
                    if (type && type.includes("NoLeaderException")) {
                        this.noLeader(true);
                        this.decisions({
                            Term: -1,
                            ObserverLog: [],
                            LeaderNode: null, 
                            Suspended: false,
                            Iteration: -1
                        });
                        loadTask.resolve();
                        return;
                    }
                }
                
                loadTask.reject(response);
            });
        return loadTask;
    }

    refresh() {
        this.spinners.refresh(true);
        return this.loadDecisions()
            .always(() => {
                this.gridController().reset(true);
                this.spinners.refresh(false)
            });
    }

    suspendObserver() {
        this.confirmationMessage("Are you sure?", "Do you want to suspend cluster observer?", ["No", "Yes, suspend"])
            .done(result => {
                if (result.can) {
                    eventsCollector.default.reportEvent("observer-log", "suspend");
                    this.spinners.toggleObserver(true);
                    new toggleClusterObserverCommand(true)
                        .execute()
                        .always(() => {
                            this.spinners.toggleObserver(false);
                            this.refresh();
                        });
                }
            });
    }

    resumeObserver() {
        this.confirmationMessage("Are you sure?", "Do you want to resume cluster observer?", ["No", "Yes, resume"])
            .done(result => {
                if (result.can) {
                    eventsCollector.default.reportEvent("observer-log", "resume");
                    this.spinners.toggleObserver(true);
                    new toggleClusterObserverCommand(false)
                        .execute()
                        .always(() => {
                            this.spinners.toggleObserver(false);
                            this.refresh();
                        });
                }
            });
    }
}

export = clusterObserverLog;
