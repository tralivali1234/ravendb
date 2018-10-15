/// <reference path="../../../../typings/tsd.d.ts"/>
import generalUtils = require("common/generalUtils");

class revisionsConfigurationEntry {

    static readonly DefaultConfiguration = "DefaultConfiguration";

    disabled = ko.observable<boolean>();
    purgeOnDelete = ko.observable<boolean>();
    collection = ko.observable<string>();

    limitRevisions = ko.observable<boolean>();
    minimumRevisionsToKeep = ko.observable<number>();

    limitRevisionsByAge = ko.observable<boolean>(false);
    minimumRevisionAgeToKeep = ko.observable<number>();

    isDefault: KnockoutComputed<boolean>;
    humaneRetentionDescription: KnockoutComputed<string>;

    validationGroup: KnockoutValidationGroup = ko.validatedObservable({
        collection: this.collection,
        minimumRevisionsToKeep: this.minimumRevisionsToKeep,
        minimumRevisionAgeToKeep: this.minimumRevisionAgeToKeep
    });

    constructor(collection: string, dto: Raven.Client.Documents.Operations.Revisions.RevisionsCollectionConfiguration) {
        this.collection(collection);

        this.limitRevisions(dto.MinimumRevisionsToKeep != null);
        this.minimumRevisionsToKeep(dto.MinimumRevisionsToKeep);

        this.limitRevisionsByAge(dto.MinimumRevisionAgeToKeep != null);
        this.minimumRevisionAgeToKeep(dto.MinimumRevisionAgeToKeep ? generalUtils.timeSpanToSeconds(dto.MinimumRevisionAgeToKeep) : null);

        this.disabled(dto.Disabled);
        this.purgeOnDelete(dto.PurgeOnDelete);
        this.isDefault = ko.pureComputed<boolean>(() => this.collection() === revisionsConfigurationEntry.DefaultConfiguration);

        this.initObservables();
        this.initValidation();
    }
    private initObservables() {
        this.humaneRetentionDescription = ko.pureComputed(() => {
            const retentionTimeHumane = generalUtils.formatTimeSpan(this.minimumRevisionAgeToKeep() * 1000, true);
            
            const agePart = this.limitRevisionsByAge() && this.minimumRevisionAgeToKeep.isValid() && this.minimumRevisionAgeToKeep() !== 0 ?
                `Revisions are going to be removed on next revision creation or document deletion once they exceed retention time of <strong>${retentionTimeHumane}</strong>` : "";
            
            const countPart = this.limitRevisions() && this.minimumRevisionsToKeep.isValid() ? 
                `At least <strong>${this.minimumRevisionsToKeep()}</strong> revisions are going to be kept` : "";
            
            return agePart + countPart;
        });

        this.limitRevisions.subscribe(() => {
            this.minimumRevisionsToKeep.clearError();
        });

        this.limitRevisionsByAge.subscribe(() => {
            this.minimumRevisionAgeToKeep.clearError();
        });
    }

    private initValidation() {
        this.collection.extend({
            required: true
        });

        this.minimumRevisionsToKeep.extend({
            required: {
                onlyIf: () => this.limitRevisions()
            },
            digit: true
        });

        this.minimumRevisionAgeToKeep.extend({
            required: {
                onlyIf: () => this.limitRevisionsByAge()
            },
            min: 0
        });
    }

    copyFrom(incoming: revisionsConfigurationEntry): this {
        this.disabled(incoming.disabled());
        this.purgeOnDelete(incoming.purgeOnDelete());
        this.collection(incoming.collection());

        this.limitRevisions(incoming.limitRevisions());
        this.minimumRevisionsToKeep(incoming.minimumRevisionsToKeep());

        this.limitRevisionsByAge(incoming.limitRevisionsByAge());
        this.minimumRevisionAgeToKeep(incoming.minimumRevisionAgeToKeep());
        
        return this;
    }

    toDto(): Raven.Client.Documents.Operations.Revisions.RevisionsCollectionConfiguration {
        return {
            Disabled: this.disabled(),
            MinimumRevisionsToKeep: this.limitRevisions() ? this.minimumRevisionsToKeep() : null,
            MinimumRevisionAgeToKeep: this.limitRevisionsByAge() ? generalUtils.formatAsTimeSpan(this.minimumRevisionAgeToKeep() * 1000) : null,
            PurgeOnDelete: this.purgeOnDelete()
        };
    }

    static empty() {
        return new revisionsConfigurationEntry("",
        {
            Disabled: false,
            MinimumRevisionsToKeep: null,
            MinimumRevisionAgeToKeep: null,
            PurgeOnDelete: false
        });
    }

    static defaultConfiguration() {
        const item = revisionsConfigurationEntry.empty();
        item.collection(revisionsConfigurationEntry.DefaultConfiguration);
        return item;
    }
}

export = revisionsConfigurationEntry;
