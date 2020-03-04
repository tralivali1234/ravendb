import app = require("durandal/app");
import viewModelBase = require("viewmodels/viewModelBase");
import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import hyperlinkColumn = require("widgets/virtualGrid/columns/hyperlinkColumn");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import generalUtils = require("common/generalUtils");
import trafficItem = require("models/resources/serverDashboard/trafficItem");
import databaseItem = require("models/resources/serverDashboard/databaseItem");
import indexingSpeed = require("models/resources/serverDashboard/indexingSpeed");
import machineResources = require("models/resources/serverDashboard/machineResources");
import driveUsage = require("models/resources/serverDashboard/driveUsage");
import clusterTopologyManager = require("common/shell/clusterTopologyManager");
import appUrl = require("common/appUrl");
import dashboardChart = require("models/resources/serverDashboard/dashboardChart");
import storagePieChart = require("models/resources/serverDashboard/storagePieChart");
import serverDashboardWebSocketClient = require("common/serverDashboardWebSocketClient");
import clusterNode = require("models/database/cluster/clusterNode");
import databasesManager = require("common/shell/databasesManager");
import createDatabase = require("viewmodels/resources/createDatabase");
import serverTime = require("common/helpers/database/serverTime");
import accessManager = require("common/shell/accessManager");

class machineResourcesSection {

    cpuChart: dashboardChart;
    memoryChart: dashboardChart;
    
    totalMemory: number;
    
    resources = ko.observable<machineResources>();

    init() {
        this.cpuChart = new dashboardChart("#cpuChart", {
            yMaxProvider: () => 100,
            topPaddingProvider: () => 2,
            tooltipProvider: data => machineResourcesSection.cpuTooltip(data)
        });

        this.memoryChart = new dashboardChart("#memoryChart", {
            yMaxProvider: () => this.totalMemory,
            topPaddingProvider: () => 2,
            tooltipProvider: data => machineResourcesSection.memoryTooltip(data, this.totalMemory)
        });
    }
    
    onResize() {
        this.cpuChart.onResize();
        this.memoryChart.onResize();
    }
    
    onData(data: Raven.Server.Dashboard.MachineResources) {
        this.totalMemory = data.TotalMemory;

        this.cpuChart.onData(moment.utc(data.Date).toDate(),
            [
                { key: "machine", value: data.MachineCpuUsage },
                { key: "process", value: data.ProcessCpuUsage }
            ]);
        this.memoryChart.onData(moment.utc(data.Date).toDate(),
            [
                { key: "machine", value: data.TotalMemory - data.AvailableMemory },
                { key: "process", value: data.ProcessMemoryUsage }
            ]);
        
        if (this.resources()) {
            this.resources().update(data);
        } else {
            this.resources(new machineResources(data));
            $('.dashboard-cpu-memory [data-toggle="tooltip"]').tooltip();
        }
    }
    
    
    private static cpuTooltip(data: dashboardChartTooltipProviderArgs) {
        if (data) {
            const date = moment(data.date).format(serverDashboard.timeFormat);
            const machine = data.values['machine'].toFixed(0) + "%";
            const process = data.values['process'].toFixed(0) + "%";
            return `<div>
                Time: <strong>${date}</strong><br />
                Machine CPU usage: <strong>${machine}</strong><br />
                Process CPU usage: <strong>${process}</strong>
                </div>`;
        }
        
        return null;
    }

    private static memoryTooltip(data: dashboardChartTooltipProviderArgs, totalMemory: number) {
        if (data) {
            const date = moment(data.date).format(serverDashboard.timeFormat);
            const physical = generalUtils.formatBytesToSize(totalMemory); 
            const machine = generalUtils.formatBytesToSize(data.values['machine']); 
            const process = generalUtils.formatBytesToSize(data.values['process']);
            return `<div>
                Time: <strong>${date}</strong><br />
                Usable physical memory: <strong>${physical}</strong><br />
                Machine memory usage: <strong>${machine}</strong><br />
                Process memory usage: <strong>${process}</strong>
                </div>`;
        }

        return null;
    }
}

class indexingSpeedSection {
    indexingChart: dashboardChart;
    reduceChart: dashboardChart;
    
    private table = [] as indexingSpeed[];
    private gridController = ko.observable<virtualGridController<indexingSpeed>>();

    totalIndexedPerSecond = ko.observable<number>(0);
    totalMappedPerSecond = ko.observable<number>(0);
    totalReducedPerSecond = ko.observable<number>(0);

    init() {
        this.indexingChart = new dashboardChart("#indexingChart", {
            tooltipProvider: data => indexingSpeedSection.indexingTooltip(data)
        });
        this.reduceChart = new dashboardChart("#reduceChart", {
            tooltipProvider: data => indexingSpeedSection.reduceTooltip(data)
        });
        
        const grid = this.gridController();

        grid.headerVisible(true);

        grid.init((s, t) => $.when({
            totalResultCount: this.table.length,
            items: this.table
        }), () => {
            return [
                //TODO:  new checkedColumn(true),
                new hyperlinkColumn<indexingSpeed>(grid, x => x.database(), x => appUrl.forIndexPerformance(x.database()), "Database", "30%"),
                new textColumn<indexingSpeed>(grid, x => x.indexedPerSecond() != null ? x.indexedPerSecond() : "n/a", "Indexed / sec", "15%", {
                    extraClass: item => item.indexedPerSecond() != null ? "" : "na"
                }),
                new textColumn<indexingSpeed>(grid, x => x.mappedPerSecond() != null ? x.mappedPerSecond() : "n/a", "Mapped / sec", "15%", {
                    extraClass: item => item.mappedPerSecond() != null ? "" : "na"
                }),
                new textColumn<indexingSpeed>(grid, x => x.reducedPerSecond() != null ? x.reducedPerSecond() : "n/a", "Entries reduced / sec", "15%", {
                    extraClass: item => item.reducedPerSecond() != null ? "" : "na"
                })
            ];
        });
    }
    
    onResize() {
        this.indexingChart.onResize();
        this.reduceChart.onResize();
    }
    
    private static indexingTooltip(data: dashboardChartTooltipProviderArgs) {
        if (data) {
            const date = moment(data.date).format(serverDashboard.timeFormat);
            const indexed = data.values['indexing'];
            return `<div>
                Time: <strong>${date}</strong><br />
                # Documents indexed/s: <strong>${indexed.toLocaleString()}</strong>
                </div>`;
        }
        
        return null;
    }
    
    private static reduceTooltip(data: dashboardChartTooltipProviderArgs) {
        if (data) {
            const date = moment(data.date).format(serverDashboard.timeFormat);
            const map = data.values['map'];
            const reduce = data.values['reduce'];
            return `<div>
                Time: <strong>${date}</strong><br />
                # Documents mapped/s: <strong>${map.toLocaleString()}</strong><br />
                # Mapped entries reduced/s: <strong>${reduce.toLocaleString()}</strong>
                </div>`;
        }
        return null;
    }
    
    onData(data: Raven.Server.Dashboard.IndexingSpeed) {
        const items = data.Items;
        items.sort((a, b) => generalUtils.sortAlphaNumeric(a.Database, b.Database));

        const newDbs = items.map(x => x.Database);
        const oldDbs = this.table.map(x => x.database());

        const removed = _.without(oldDbs, ...newDbs);
        removed.forEach(dbName => {
            const matched = this.table.find(x => x.database() === dbName);
            _.pull(this.table, matched);
        });

        items.forEach(incomingItem => {
            const matched = this.table.find(x => x.database() === incomingItem.Database);
            if (matched) {
                matched.update(incomingItem);
            } else {
                this.table.push(new indexingSpeed(incomingItem));
            }
        });

        this.updateTotals();
        
        this.indexingChart.onData(moment.utc(data.Date).toDate(), [{
            key: "indexing", value: this.totalIndexedPerSecond() 
        }]);
        
        this.reduceChart.onData(moment.utc(data.Date).toDate(), [
            { key: "map", value: this.totalMappedPerSecond() },
            { key: "reduce", value: this.totalReducedPerSecond() }
        ]);

        this.gridController().reset(false);
    }

    private updateTotals() {
        let totalIndexed = 0;
        let totalMapped = 0;
        let totalReduced = 0;

        this.table.forEach(item => {
            totalIndexed += item.indexedPerSecond() || 0;
            totalMapped += item.mappedPerSecond() || 0;
            totalReduced += item.reducedPerSecond() || 0;
        });

        this.totalIndexedPerSecond(totalIndexed);
        this.totalMappedPerSecond(totalMapped);
        this.totalReducedPerSecond(totalReduced);
    }
}

class databasesSection {
    private table = [] as databaseItem[];
    private gridController = ko.observable<virtualGridController<databaseItem>>();
    
    totalOfflineDatabases = ko.observable<number>(0);
    totalOnlineDatabases = ko.observable<number>(0);
    totalDatabases: KnockoutComputed<number>;
    
    constructor() {
        this.totalDatabases = ko.pureComputed(() => this.totalOnlineDatabases() + this.totalOfflineDatabases());
    }
    
    init() {
        const grid = this.gridController();

        grid.headerVisible(true);

        grid.init((s, t) => $.when({
            totalResultCount: this.table.length,
            items: this.table
        }), () => {
            return [
                new hyperlinkColumn<databaseItem>(grid, x => x.database(), x => appUrl.forDocuments(null, x.database()), "Database", "30%"), 
                new textColumn<databaseItem>(grid, x => x.documentsCount(), "Docs #", "25%"),
                new textColumn<databaseItem>(grid, 
                        x => x.indexesCount() + ( x.erroredIndexesCount() ? ' (<span class=\'text-danger\'>' + x.erroredIndexesCount() + '</span>)' : '' ), 
                        "Index # (Error #)", 
                        "20%",
                        {
                            useRawValue: () => true
                        }),
                new textColumn<databaseItem>(grid, x => x.alertsCount(), "Alerts #", "12%", {
                    extraClass: item => item.alertsCount() ? 'has-alerts' : ''
                }), 
                new textColumn<databaseItem>(grid, x => x.replicationFactor(), "Replica factor", "12%")
            ];
        });
    }
    
    onData(data: Raven.Server.Dashboard.DatabasesInfo) {
        const items = data.Items;
        items.sort((a, b) => generalUtils.sortAlphaNumeric(a.Database, b.Database));

        const newDbs = items.map(x => x.Database);
        const oldDbs = this.table.map(x => x.database());

        const removed = _.without(oldDbs, ...newDbs);
        removed.forEach(dbName => {
            const matched = this.table.find(x => x.database() === dbName);
            _.pull(this.table, matched);
        });

        items.forEach(incomingItem => {
            const matched = this.table.find(x => x.database() === incomingItem.Database);
            if (matched) {
                matched.update(incomingItem);
            } else {
                this.table.push(new databaseItem(incomingItem));
            }
        });
        
        this.updateTotals();
        
        this.gridController().reset(false);
    }
    
    private updateTotals() {
        let totalOnline = 0;
        let totalOffline = 0;
        
        this.table.forEach(item => {
            if (item.online()) {
                totalOnline++;
            } else {
                totalOffline++;
            }
        });
        
        this.totalOnlineDatabases(totalOnline);
        this.totalOfflineDatabases(totalOffline);
    }
}

class trafficSection {
    private sizeFormatter = generalUtils.formatBytesToSize;
    
    private table = [] as trafficItem[];
    private trafficChart: dashboardChart;

    private gridController = ko.observable<virtualGridController<trafficItem>>();
    
    totalRequestsPerSecond = ko.observable<number>(0);
    totalWritesPerSecond = ko.observable<number>(0);
    totalDataWritesPerSecond = ko.observable<number>(0);
    
    init()  {
        const grid = this.gridController();

        grid.headerVisible(true);
        
        grid.init((s, t) => $.when({
            totalResultCount: this.table.length,
            items: this.table
        }), () => {
            return [
                //TODO: new checkedColumn(true),
                new hyperlinkColumn<trafficItem>(grid, x => x.database(), x => appUrl.forTrafficWatch(x.database()), "Database", "30%"),
                new textColumn<trafficItem>(grid, x => x.requestsPerSecond(), "Requests / s", "20%"),
                new textColumn<trafficItem>(grid, x => x.writesPerSecond(), "Writes / s", "25%"),
                new textColumn<trafficItem>(grid, x => this.sizeFormatter(x.dataWritesPerSecond()), "Data written / s", "25%")
            ];
        });
        
        this.trafficChart = new dashboardChart("#trafficChart", {
            useSeparateYScales: true,
            topPaddingProvider: key => {
                switch (key) {
                    case "written":
                        return 30;
                    case "writes":
                        return 20;
                    default:
                        return 5;
                }
            },
            tooltipProvider: data => this.trafficTooltip(data)
        });
    }
    
    onResize() {
        this.trafficChart.onResize();
        this.gridController().reset(true);
    }

    private trafficTooltip(data: dashboardChartTooltipProviderArgs) {
        if (data) {
            const date = moment(data.date).format(serverDashboard.timeFormat);
            const requests = data.values['requests'];
            const writes = data.values['writes'];
            const written = data.values['written'];

            return `<div>
                Time: <strong>${date}</strong><br />
                Requests/s: <strong>${requests.toLocaleString()}</strong><br />
                Writes/s: <strong>${writes.toLocaleString()}</strong><br />
                Data Written/s: <strong>${this.sizeFormatter(written)}</strong>
                </div>`;
        }
        return null;
    }
    
    onData(data: Raven.Server.Dashboard.TrafficWatch) {
        const items = data.Items;
        items.sort((a, b) => generalUtils.sortAlphaNumeric(a.Database, b.Database));

        const newDbs = items.map(x => x.Database);
        const oldDbs = this.table.map(x => x.database());
        
        const removed = _.without(oldDbs, ...newDbs);
        removed.forEach(dbName => {
           const matched = this.table.find(x => x.database() === dbName);
           _.pull(this.table, matched);
        });
        
        items.forEach(incomingItem => {
            const matched = this.table.find(x => x.database() === incomingItem.Database);
            if (matched) {
                matched.update(incomingItem);
            } else {
                this.table.push(new trafficItem(incomingItem));
            }
        });
        
        this.updateTotals();
        
        this.trafficChart.onData(moment.utc(data.Date).toDate(), [{
            key: "writes",
            value: this.totalWritesPerSecond()
        }, {
            key: "written",
            value: this.totalDataWritesPerSecond()
        },{
            key: "requests",
            value: this.totalRequestsPerSecond()
        }]);
        
        this.gridController().reset(false);
    }
    
    private updateTotals() {
        let totalRequests = 0;
        let writesPerSecond = 0;
        let dataWritesPerSecond = 0;

        this.table.forEach(item => {
            totalRequests += item.requestsPerSecond();
            writesPerSecond += item.writesPerSecond();
            dataWritesPerSecond += item.dataWritesPerSecond();
        });

        this.totalRequestsPerSecond(totalRequests);
        this.totalWritesPerSecond(writesPerSecond);
        this.totalDataWritesPerSecond(dataWritesPerSecond);
    }
}

class driveUsageSection {
    private data: Raven.Server.Dashboard.DrivesUsage;
    private table = ko.observableArray<driveUsage>();
    private storageChart: storagePieChart;
    
    includeTemporaryBuffers = ko.observable<boolean>(true);
    
    totalDocumentsSize: KnockoutComputed<number>;
    
    constructor() {
        this.totalDocumentsSize = ko.pureComputed(() => {
            return _.sum(this.table().map(x => x.totalDocumentsSpaceUsed()));
        });
    }
    
    init() {
        this.storageChart = new storagePieChart("#storageChart");
        
        this.includeTemporaryBuffers.subscribe(() => {
            this.table().forEach(item => {
                item.gridController().reset(true);
            });
            this.updateChart(this.data, true);
        });
    }
    
    onResize() {
        this.table().forEach(item => {
            item.gridController().reset(true);
        });
        
        this.storageChart.onResize();
    }
    
    onData(data: Raven.Server.Dashboard.DrivesUsage) {
        this.data = data;
        const items = data.Items;

        this.updateChart(data, false);

        const newMountPoints = items.map(x => x.MountPoint);
        const oldMountPoints = this.table().map(x => x.mountPoint());

        const removed = _.without(oldMountPoints, ...newMountPoints);
        removed.forEach(name => {
            const matched = this.table().find(x => x.mountPoint() === name);
            this.table.remove(matched);
        });

        items.forEach(incomingItem => {
            const matched = this.table().find(x => x.mountPoint() === incomingItem.MountPoint);
            if (matched) {
                matched.update(incomingItem);
            } else {
                const usage = new driveUsage(incomingItem, this.storageChart.getColorProvider(), this.includeTemporaryBuffers);
                this.table.push(usage);
            }
        });
    }
    
    private updateChart(data: Raven.Server.Dashboard.DrivesUsage, withTween: boolean) {
        const cache = new Map<string, number>();

        const includeTemp = this.includeTemporaryBuffers();
        
        // group by database size
        data.Items.forEach(mountPointUsage => {
            mountPointUsage.Items.forEach(item => {
                const sizeToUse = includeTemp ? item.Size + item.TempBuffersSize : item.Size;
                
                if (cache.has(item.Database)) {
                    cache.set(item.Database, sizeToUse + cache.get(item.Database));
                } else {
                    cache.set(item.Database, sizeToUse);
                }
            });
        });
        
        const result = [] as Array<{ Database: string, Size: number }>;
        
        cache.forEach((value, key) => {
            result.push({
                Database: key,
                Size: value,
            });
        });

        this.storageChart.onData(result, withTween);
    }
}

class serverDashboard extends viewModelBase {
    
    static readonly dateFormat = generalUtils.dateFormat;
    static readonly timeFormat = "h:mm:ss A";
    liveClient = ko.observable<serverDashboardWebSocketClient>();
    
    spinners = {
        loading: ko.observable<boolean>(true)
    };
    
    clusterManager = clusterTopologyManager.default;
    accessManager = accessManager.default.dashboardView;
    
    formattedUpTime: KnockoutComputed<string>;
    formattedStartTime: KnockoutComputed<string>;
    node: KnockoutComputed<clusterNode>;
    sizeFormatter = generalUtils.formatBytesToSize;

    usingHttps = location.protocol === "https:";

    certificatesUrl = appUrl.forCertificates();
    
    trafficSection = new trafficSection();
    databasesSection = new databasesSection();
    indexingSpeedSection = new indexingSpeedSection();
    machineResourcesSection = new machineResourcesSection();
    driveUsageSection = new driveUsageSection();
    
    noDatabases = ko.pureComputed(() => databasesManager.default.databases().length === 0);

    constructor() {
        super();

        this.formattedUpTime = ko.pureComputed(() => {
            const startTime = serverTime.default.startUpTime();
            if (!startTime) {
                return "a few seconds";
            }

            return generalUtils.formatDurationByDate(startTime, true);
        });

        this.formattedStartTime = ko.pureComputed(() => {
            const start = serverTime.default.startUpTime();
            return start ? start.local().format(serverDashboard.dateFormat) : "";
        });

        this.node = ko.pureComputed(() => {
            const topology = this.clusterManager.topology();
            const nodeTag = topology.nodeTag();
            return topology.nodes().find(x => x.tag() === nodeTag);
        });
    }

    compositionComplete() {
        super.compositionComplete();

        this.initSections();
        
        this.enableLiveView();
    }

    private initSections() {
        this.trafficSection.init();
        this.databasesSection.init();
        this.indexingSpeedSection.init();
        this.machineResourcesSection.init();
        this.driveUsageSection.init();
        
        this.registerDisposableHandler($(window), "resize", _.debounce(() => this.onResize(), 700));
    }
    
    private onResize() {
        this.trafficSection.onResize();
        this.indexingSpeedSection.onResize();
        this.machineResourcesSection.onResize();
        this.driveUsageSection.onResize();
    }
    
    deactivate() {
        super.deactivate();
        
        if (this.liveClient()) {
            this.liveClient().dispose();
        }
    }
    
    private enableLiveView() {
        this.liveClient(new serverDashboardWebSocketClient(d => this.onData(d)));
    }

    private onData(data: Raven.Server.Dashboard.AbstractDashboardNotification) {
        this.spinners.loading(false);
        
        switch (data.Type) {
            case "DriveUsage":
                this.driveUsageSection.onData(data as Raven.Server.Dashboard.DrivesUsage);
                break;
            case "MachineResources":
                this.machineResourcesSection.onData(data as Raven.Server.Dashboard.MachineResources);
                break;
            case "TrafficWatch":
                this.trafficSection.onData(data as Raven.Server.Dashboard.TrafficWatch);
                break;
            case "DatabasesInfo":
                this.databasesSection.onData(data as Raven.Server.Dashboard.DatabasesInfo);
                break;
            case "IndexingSpeed":
                this.indexingSpeedSection.onData(data as Raven.Server.Dashboard.IndexingSpeed);
                break;
            default:
                throw new Error("Unhandled notification type: " + data.Type);
        }
    }
    
    newDatabase() {
        const createDbView = new createDatabase("newDatabase");
        app.showBootstrapDialog(createDbView);
    }
}

export = serverDashboard;
