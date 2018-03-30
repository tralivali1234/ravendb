import app = require("durandal/app");
import operation = require("common/notifications/models/operation");
import abstractNotification = require("common/notifications/models/abstractNotification");
import notificationCenter = require("common/notifications/notificationCenter");
import abstractOperationDetails = require("viewmodels/common/notificationCenter/detailViewer/operations/abstractOperationDetails");

type smugglerListItemStatus = "processed" | "skipped" | "processing" | "pending";

type smugglerListItem = {
    name: string;
    stage: smugglerListItemStatus;
    hasReadCount: boolean;
    readCount: string;
    hasErroredCount: boolean;
    erroredCount: string;
    hasSkippedCount: boolean;
    skippedCount: string;
    hasAttachments: boolean;
    attachments: attachmentsListItem;
    processingSpeedText: string;
}

type attachmentsListItem = {
    readCount: string;
    erroredCount: string;
}

class smugglerDatabaseDetails extends abstractOperationDetails {

    static extractingDataStageName = "Extracting data";
    
    detailsVisible = ko.observable<boolean>(false);
    tail = ko.observable<boolean>(false);
    lastDocsCount = 0;
    lastProcessingSpeedText = "Processing";

    exportItems: KnockoutComputed<Array<smugglerListItem>>;
    messages: KnockoutComputed<Array<string>>;
    messagesJoined: KnockoutComputed<string>;
    previousProgressMessages: string[];
    processingSpeed: KnockoutComputed<string>;

    constructor(op: operation, notificationCenter: notificationCenter) {
        super(op, notificationCenter);
        this.bindToCurrentInstance("toggleDetails");

        this.initObservables();
    }

    protected initObservables() {
        super.initObservables();

        this.exportItems = ko.pureComputed(() => {
            if (this.op.status() === "Faulted") {
                return [];
            }

            const status = (this.op.isCompleted() ? this.op.result() : this.op.progress()) as Raven.Client.Documents.Smuggler.SmugglerProgressBase;

            if (!status) {
                return [];
            }

            const result = [] as Array<smugglerListItem>;
            if ("SnapshotRestore" in status) {
                const restoreCounts = (status as Raven.Server.Documents.PeriodicBackup.RestoreProgress).SnapshotRestore;
                
                // skip it this case means it is not backup progress object or it is restore of non-binary data 
                if (!restoreCounts.Skipped) {
                    result.push(this.mapToExportListItem("Preparing restore", restoreCounts));
                }
            }
            
            if (this.op.taskType() === "MigrationFromLegacyData") {
                const migrationCounts = (status as Raven.Client.Documents.Smuggler.OfflineMigrationProgress).DataExporter;
                result.push(this.mapToExportListItem(smugglerDatabaseDetails.extractingDataStageName, migrationCounts));
            }
            
            if (this.op.taskType() === "CollectionImportFromCsv") {
                result.push(this.mapToExportListItem("Documents", status.Documents, false));
            } else {
                result.push(this.mapToExportListItem("Documents", status.Documents, true));
                result.push(this.mapToExportListItem("Conflicts", status.Conflicts));
                result.push(this.mapToExportListItem("Revisions", status.RevisionDocuments, true));
                result.push(this.mapToExportListItem("Indexes", status.Indexes));
                result.push(this.mapToExportListItem("Identities", status.Identities));
                result.push(this.mapToExportListItem("Compare Exchange", status.CompareExchange));
            }
            
            let shouldUpdateToPending = false;
            result.forEach(item => {
                if (item.stage === "processing") {
                    if (shouldUpdateToPending) {
                        item.stage = "pending";
                    }

                    shouldUpdateToPending = true;
                }

                if (item.stage === "pending" || item.stage === "skipped" || item.name === smugglerDatabaseDetails.extractingDataStageName) {
                    item.hasReadCount = false;
                    item.hasErroredCount = false;
                    item.hasSkippedCount = false;
                    item.readCount = "-";
                    item.erroredCount = "-";
                    item.skippedCount = item.skippedCount || "-";

                    if (item.hasAttachments) {
                        const attachments = item.attachments;
                        attachments.erroredCount = "-";
                        attachments.readCount = "-";
                    }
                }
            });

            return result;
        });

        this.messages = ko.pureComputed(() => {
            if (this.operationFailed()) {
                const errors = this.errorMessages();
                const previousMessages = this.previousProgressMessages || [];
                return previousMessages.concat(...errors);
            } else if (this.op.isCompleted()) {
                const result = this.op.result() as Raven.Client.Documents.Smuggler.SmugglerResult;
                return result ? result.Messages : [];
            } else {
                const progress = this.op.progress() as Raven.Client.Documents.Smuggler.SmugglerResult;
                if (progress) {
                    this.previousProgressMessages = progress.Messages;
                }
                return progress ? progress.Messages : [];
            }
        });

        this.messagesJoined = ko.pureComputed(() => this.messages() ? this.messages().join("\n") : "");
        
        this.registerDisposable(this.messages.subscribe(() => {
            if (this.tail()) {
                this.scrollDown();
            }
        }));

        this.registerDisposable(this.tail.subscribe(enabled => {
            if (enabled) {
                this.scrollDown();
            }
        }));

        this.registerDisposable(this.operationFailed.subscribe(failed => {
            if (failed) {
                this.detailsVisible(true);
            }
        }));

        if (this.operationFailed()) {
            this.detailsVisible(true);
        }
    }

    private scrollDown() {
        const messages = $(".export-messages")[0];
        messages.scrollTop = messages.scrollHeight;
    }

    toggleDetails() {
        this.detailsVisible(!this.detailsVisible());
    }

    private mapToExportListItem(name: string, item: Raven.Client.Documents.Smuggler.SmugglerProgressBase.Counts, hasAttachments: boolean = false): smugglerListItem {
        let stage: smugglerListItemStatus = "processing";
        if (item.Skipped) {
            stage = "skipped";
        } else if (item.Processed) {
            stage = "processed";
        }

        let attachmentsItem = null as attachmentsListItem;
        if (hasAttachments) {
            const attachments = (item as Raven.Client.Documents.Smuggler.SmugglerProgressBase.CountsWithLastEtag).Attachments;
            attachmentsItem = {
                readCount: attachments.ReadCount.toLocaleString(),
                erroredCount: attachments.ErroredCount.toLocaleString()
            }
        }

        const isDocuments = name === "Documents";
        let processingSpeedText = "Processing";
        const skippedCount = isDocuments ? (item as Raven.Client.Documents.Smuggler.SmugglerProgressBase.CountsWithSkippedCountAndLastEtag).SkippedCount : 0;
        if (isDocuments) {
            const docsCount = item.ReadCount + skippedCount + item.ErroredCount;
            if (docsCount === this.lastDocsCount) {
                processingSpeedText = this.lastProcessingSpeedText;
            } else {
                this.lastDocsCount = docsCount;
                const processingSpeed = this.calculateProcessingSpeed(item.ReadCount + skippedCount + item.ErroredCount);
                if (processingSpeed > 0) {
                    processingSpeedText = this.lastProcessingSpeedText = processingSpeed.toLocaleString() + " docs / sec";
                }
            }
        }

        return {
            name: name,
            stage: stage,
            hasReadCount: true, // it will be reassigned in post-processing
            readCount: item.ReadCount.toLocaleString(),
            hasSkippedCount: isDocuments,
            skippedCount: isDocuments ? skippedCount.toLocaleString() : "-",
            hasErroredCount: true, // it will be reassigned in post-processing
            erroredCount: item.ErroredCount.toLocaleString(),
            hasAttachments: hasAttachments,
            attachments: attachmentsItem,
            processingSpeedText: processingSpeedText
        } as smugglerListItem;
    }

    static supportsDetailsFor(notification: abstractNotification) {
        return (notification instanceof operation) &&
        (notification.taskType() === "DatabaseExport" ||
            notification.taskType() === "DatabaseImport" ||
            notification.taskType() === "DatabaseMigration" ||
            notification.taskType() === "DatabaseRestore" ||
            notification.taskType() === "MigrationFromLegacyData" ||
            notification.taskType() === "CollectionImportFromCsv"
        );
    }

    static showDetailsFor(op: operation, center: notificationCenter) {
        return app.showBootstrapDialog(new smugglerDatabaseDetails(op, center));
    }

    static merge(existing: operation, incoming: Raven.Server.NotificationCenter.Notifications.OperationChanged): void {
        if (!smugglerDatabaseDetails.supportsDetailsFor(existing)) {
            return;
        }

        const isUpdate = !_.isUndefined(incoming);

        if (!isUpdate) {
            // object was just created  - only copy message -> message field

            if (!existing.isCompleted()) {
                const result = existing.progress() as Raven.Client.Documents.Smuggler.SmugglerResult;
                result.Messages = [result.Message];
            }
            
        } else if (incoming.State.Status === "InProgress") { // if incoming operaton is in progress, then merge messages into existing item
            const incomingResult = incoming.State.Progress as Raven.Client.Documents.Smuggler.SmugglerResult;
            const existingResult = existing.progress() as Raven.Client.Documents.Smuggler.SmugglerResult;

            incomingResult.Messages = existingResult.Messages.concat(incomingResult.Message);
        }

        if (isUpdate) {
            existing.updateWith(incoming);
        }
    }
}

export = smugglerDatabaseDetails;
