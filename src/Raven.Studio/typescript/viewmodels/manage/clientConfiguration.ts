import viewModelBase = require("viewmodels/viewModelBase");
import clientConfigurationModel = require("models/database/settings/clientConfigurationModel");
import saveGlobalClientConfigurationCommand = require("commands/resources/saveGlobalClientConfigurationCommand");
import getGlobalClientConfigurationCommand = require("commands/resources/getGlobalClientConfigurationCommand");
import eventsCollector = require("common/eventsCollector");

class clientConfiguration extends viewModelBase {

    model: clientConfigurationModel;
    
    spinners = {
        save: ko.observable<boolean>(false)
    };
    
    activate(args: any) {
        super.activate(args);
        
        this.bindToCurrentInstance("saveConfiguration", "setReadMode");
        
        return new getGlobalClientConfigurationCommand()
            .execute()
            .done((dto) => {
                this.model = new clientConfigurationModel(dto);
            });
    }

    compositionComplete() {
        super.compositionComplete();
        this.initValidation();
    }

    private initValidation() {
        this.model.readBalanceBehavior.extend({
            required: {
                onlyIf: () => _.includes(this.model.isDefined(), "readBalanceBehavior")
            }
        });
        
        this.model.maxNumberOfRequestsPerSession.extend({
            required: {
                onlyIf: () => _.includes(this.model.isDefined(), "maxNumberOfRequestsPerSession")
            },
            digit: true
        })
    }
    
    saveConfiguration() {
        if (!this.isValid(this.model.validationGroup)) {
            return;
        }
        
        eventsCollector.default.reportEvent("client-configuration", "save");
        
        this.spinners.save(true);
        this.model.disabled(this.model.isDefined().length === 0);
        
        new saveGlobalClientConfigurationCommand(this.model.toDto())
            .execute()
            .always(() => this.spinners.save(false));
    }

    setReadMode(mode: Raven.Client.Http.ReadBalanceBehavior) {
        this.model.readBalanceBehavior(mode);
    }
}

export = clientConfiguration;
