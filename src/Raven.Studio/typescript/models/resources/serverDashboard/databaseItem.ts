/// <reference path="../../../../typings/tsd.d.ts"/>

class databaseItem {
    database = ko.observable<string>();
    
    documentsCount = ko.observable<number>();
    indexesCount = ko.observable<number>();
    disabled = ko.observable<boolean>();
    erroredIndexesCount = ko.observable<number>();
    alertsCount = ko.observable<number>();
    replicationFactor = ko.observable<number>();
    online = ko.observable<boolean>();
    
    constructor(dto: Raven.Server.Dashboard.DatabaseInfoItem) {
        this.update(dto);
    }
    
    update(dto: Raven.Server.Dashboard.DatabaseInfoItem) {
        this.database(dto.Database);
        this.documentsCount(dto.DocumentsCount);
        this.indexesCount(dto.IndexesCount);
        this.disabled(dto.Disabled);
        this.erroredIndexesCount(dto.ErroredIndexesCount);
        this.alertsCount(dto.AlertsCount);
        this.replicationFactor(dto.ReplicationFactor);
        this.online(dto.Online);
    }
}

export = databaseItem;
