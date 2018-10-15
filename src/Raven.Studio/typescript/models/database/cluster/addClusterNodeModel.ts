/// <reference path="../../../../typings/tsd.d.ts"/>

class addClusterNodeModel {
    serverUrl = ko.observable<string>();
    nodeTag = ko.observable<string>();
    addAsWatcher = ko.observable<boolean>(false);
    assignedCores = ko.observable<number>(undefined);
    usaAvailableCores = ko.observable<boolean>(true);

    validationGroup: KnockoutValidationGroup = ko.validatedObservable({
        serverUrl: this.serverUrl,
        nodeTag: this.nodeTag,
        assignedCores: this.assignedCores
    });

    constructor() {
        this.usaAvailableCores.subscribe(newValue => {
            this.assignedCores.clearError();
            if (!newValue)
                return;

            this.assignedCores(undefined);
        });

        this.initValidation();
    }

    private initValidation() {
        this.serverUrl.extend({
            required: true,
            validUrl: true
        });

        this.nodeTag.extend({
            pattern: {
                message: 'Node tag must contain only upper case letters.',
                params: '^[A-Z]+$'
            },
            minLength: 1,
            maxLength: 4,
        });

        this.assignedCores.extend({
            required: {
                onlyIf: () => !this.usaAvailableCores()
            },
            min: 1
        });
    }
}

export = addClusterNodeModel;
