﻿import amazonSettings = require("models/database/tasks/periodicBackup/amazonSettings");
import jsonUtil = require("common/jsonUtil");

class glacierSettings extends amazonSettings {
    vaultName = ko.observable<string>();

    constructor(dto: Raven.Client.Documents.Operations.Backups.GlacierSettings, allowedRegions: Array<string>) {
        super(dto, "Glacier", allowedRegions);

        this.vaultName(dto.VaultName);

        this.initValidation();

        this.dirtyFlag = new ko.DirtyFlag([
            this.enabled,
            this.vaultName,
            this.awsAccessKey,
            this.awsSecretKey,
            this.awsRegionName,
            this. selectedAwsRegion
        ], false, jsonUtil.newLineNormalizingHashFunction);
    }

    initValidation() {
        // - vault name can be between 1 and 255 characters long.
        // - allowed characters are a-z, A-Z, 0-9, '_' (underscore), '-' (hyphen), and '.' (period).
        const regExp = /^[A-Za-z0-9_\.-]+$/;

        this.vaultName.extend({
            validation: [
                {
                    validator: (vaultName: string) => this.validate(() =>
                        vaultName && vaultName.length >= 1 && vaultName.length <= 255),
                    message: "Vault name must be at least 1 character and no more than 255 characters long"
                },
                {
                    validator: (vaultName: string) => this.validate(() => regExp.test(vaultName)),
                    message: "Allowed characters are a-z, A-Z, 0-9, '_' (underscore), '-' (hyphen), and '.' (period)"
                }
            ]
        });

        this.validationGroup = ko.validatedObservable({
            awsAccessKey: this.awsAccessKey,
            awsSecretKey: this.awsSecretKey,
            awsRegionName: this.awsRegionName,
            vaultName: this.vaultName
        });
    }

    toDto(): Raven.Client.Documents.Operations.Backups.GlacierSettings {
        const dto = super.toDto() as Raven.Client.Documents.Operations.Backups.GlacierSettings;
        dto.VaultName = this.vaultName();
        return dto;
    }

    static empty(allowedRegions: Array<string>): glacierSettings {
        return new glacierSettings({
            Disabled: true,
            AwsAccessKey: null,
            AwsRegionName: null,
            AwsSecretKey: null,
            VaultName: null
        }, allowedRegions);
    }
}

export = glacierSettings;
