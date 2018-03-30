import app = require("durandal/app");
import router = require("plugins/router");
import viewModelBase = require("viewmodels/viewModelBase");
import dialog = require("plugins/dialog");
import indexDefinition = require("models/database/index/indexDefinition");
import autoIndexDefinition = require("models/database/index/autoIndexDefinition");
import getIndexDefinitionCommand = require("commands/database/index/getIndexDefinitionCommand");
import getCSharpIndexDefinitionCommand = require("commands/database/index/getCSharpIndexDefinitionCommand");
import appUrl = require("common/appUrl");
import jsonUtil = require("common/jsonUtil");
import aceEditorBindingHandler = require("common/bindingHelpers/aceEditorBindingHandler");
import messagePublisher = require("common/messagePublisher");
import autoCompleteBindingHandler = require("common/bindingHelpers/autoCompleteBindingHandler");
import indexAceAutoCompleteProvider = require("models/database/index/indexAceAutoCompleteProvider");
import deleteIndexesConfirm = require("viewmodels/database/indexes/deleteIndexesConfirm");
import saveIndexDefinitionCommand = require("commands/database/index/saveIndexDefinitionCommand");
import indexFieldOptions = require("models/database/index/indexFieldOptions");
import getIndexFieldsFromMapCommand = require("commands/database/index/getIndexFieldsFromMapCommand");
import configurationItem = require("models/database/index/configurationItem");
import getIndexNamesCommand = require("commands/database/index/getIndexNamesCommand");
import eventsCollector = require("common/eventsCollector");
import popoverUtils = require("common/popoverUtils");
import showDataDialog = require("viewmodels/common/showDataDialog");
import formatIndexCommand = require("commands/database/index/formatIndexCommand");
import additionalSource = require("models/database/index/additionalSource");

class editIndex extends viewModelBase {

    static readonly $body = $("body");

    isEditingExistingIndex = ko.observable<boolean>(false);
    editedIndex = ko.observable<indexDefinition>();
    isAutoIndex = ko.observable<boolean>(false);

    originalIndexName: string;
    isSaveEnabled: KnockoutComputed<boolean>;
    saveInProgress = ko.observable<boolean>(false);
    indexAutoCompleter: indexAceAutoCompleteProvider;
    nameChanged: KnockoutComputed<boolean>;
    canEditIndexName: KnockoutComputed<boolean>;

    fieldNames = ko.observableArray<string>([]);
    indexNameHasFocus = ko.observable<boolean>(false);

    private indexesNames = ko.observableArray<string>();

    queryUrl = ko.observable<string>();
    termsUrl = ko.observable<string>();
    indexesUrl = ko.pureComputed(() => this.appUrls.indexes());
    
    selectedSourcePreview = ko.observable<additionalSource>();
    additionalSourcePreviewHtml: KnockoutComputed<string>;

    /* TODO
    canSaveSideBySideIndex: KnockoutComputed<boolean>;
     mergeSuggestion = ko.observable<indexMergeSuggestion>(null);
    */

    constructor() {
        super();

        this.bindToCurrentInstance("removeMap", 
            "removeField", 
            "createFieldNameAutocompleter", 
            "removeConfigurationOption", 
            "formatIndex", 
            "deleteAdditionalSource", 
            "previewAdditionalSource");

        aceEditorBindingHandler.install();
        autoCompleteBindingHandler.install();

        this.initializeObservables();

        /* TODO: side by side
        this.canSaveSideBySideIndex = ko.computed(() => {
            if (!this.isEditingExistingIndex()) {
                return false;
            }
            var loadedIndex = this.loadedIndexName(); // use loaded index name
            var editedName = this.editedIndex().name();
            return loadedIndex === editedName;
        });*/
    }

    private initializeObservables() {
        this.editedIndex.subscribe(indexDef => {
            const firstMap = indexDef.maps()[0].map;

            firstMap.throttle(1000).subscribe(() => {
                this.updateIndexFields();
            });
        });

        this.canEditIndexName = ko.pureComputed(() => {
            return !this.isEditingExistingIndex();
        });

        this.nameChanged = ko.pureComputed(() => {
            const newName = this.editedIndex().name();
            const oldName = this.originalIndexName;

            return newName !== oldName;
        });
        
        this.additionalSourcePreviewHtml = ko.pureComputed(() => {
            const source = this.selectedSourcePreview();
            if (source) {
                return '<pre class="form-control sourcePreview">' + Prism.highlight(source.code(), (Prism.languages as any).csharp) + '</pre>';
            } else { 
                return `<div class="sourcePreview"><i class="icon-xl icon-empty-set text-muted"></i><h2 class="text-center">No Additional sources uploaded</h2></div>`;
            }
        })
    }

    canActivate(indexToEdit: string): JQueryPromise<canActivateResultDto> {
        const indexToEditName = indexToEdit || undefined;
        
        super.canActivate(indexToEditName);

        const db = this.activeDatabase();

        if (indexToEditName) {
            this.isEditingExistingIndex(true);
            const canActivateResult = $.Deferred<canActivateResultDto>();
            this.fetchIndexToEdit(indexToEditName)
                .done(() => canActivateResult.resolve({ can: true }))
                .fail(() => {
                    messagePublisher.reportError("Could not find " + indexToEditName + " index");
                    canActivateResult.resolve({ redirect: appUrl.forIndexes(db) });
                });
            return canActivateResult;
        } else {
            this.editedIndex(indexDefinition.empty());
        }

        return $.Deferred<canActivateResultDto>().resolve({ can: true });

    }

    activate(indexToEditName: string) {
        super.activate(indexToEditName);

        if (this.isEditingExistingIndex()) {
            this.editExistingIndex(indexToEditName);
        }

        this.updateHelpLink('CQ5AYO');

        this.initializeDirtyFlag();
        this.indexAutoCompleter = new indexAceAutoCompleteProvider(this.activeDatabase(), this.editedIndex);

        this.initValidation();
        this.fetchIndexes();
    }

    private initValidation() {

        //TODO: aceValidation: true for map and reduce

        this.editedIndex().name.extend({
            validation: [
                {
                    validator: (val: string) => {
                        return val === this.originalIndexName || !_.includes(this.indexesNames(), val);
                    },
                    message: "Already being used by an existing index."
                }]
        });
    }

    private fetchIndexes() {
        const db = this.activeDatabase();
        new getIndexNamesCommand(db)
            .execute()
            .done((indexesNames) => {
                this.indexesNames(indexesNames);
            });
    }

    attached() {
        super.attached();
        this.addMapHelpPopover();
        this.addReduceHelpPopover();
        this.addAdditionalSourcesPopover();
    }

    private updateIndexFields() {
        const map = this.editedIndex().maps()[0].map();
        new getIndexFieldsFromMapCommand(this.activeDatabase(), map)
            .execute()
            .done((fields: resultsDto<string>) => {
                this.fieldNames(fields.Results);
            });
    }

    private initializeDirtyFlag() {
        const indexDef: indexDefinition = this.editedIndex();
        const checkedFieldsArray: Array<KnockoutObservable<any>> = [indexDef.name, indexDef.maps, indexDef.reduce, indexDef.numberOfFields, indexDef.outputReduceToCollection];

        const configuration = indexDef.configuration();
        if (configuration) {
            checkedFieldsArray.push(indexDef.numberOfConfigurationFields);

            configuration.forEach(configItem => {
                checkedFieldsArray.push(configItem.key);
                checkedFieldsArray.push(configItem.value);
            });
        }

        const addDirtyFlagInput = (field: indexFieldOptions) => {
            checkedFieldsArray.push(field.name);
            checkedFieldsArray.push(field.analyzer);
            checkedFieldsArray.push(field.indexing);
            checkedFieldsArray.push(field.storage);
            checkedFieldsArray.push(field.suggestions);
            checkedFieldsArray.push(field.termVector);
            checkedFieldsArray.push(field.hasSpatialOptions);

            const spatial = field.spatial();
            if (spatial) {
                checkedFieldsArray.push(spatial.type);
                checkedFieldsArray.push(spatial.strategy);
                checkedFieldsArray.push(spatial.maxTreeLevel);
                checkedFieldsArray.push(spatial.minX);
                checkedFieldsArray.push(spatial.maxX);
                checkedFieldsArray.push(spatial.minY);
                checkedFieldsArray.push(spatial.maxY);
                checkedFieldsArray.push(spatial.units);
            }
        };

        indexDef.fields().forEach(field => addDirtyFlagInput(field));

        const hasDefaultFieldOptions = ko.pureComputed(() => !!indexDef.defaultFieldOptions());
        checkedFieldsArray.push(hasDefaultFieldOptions);
        
        checkedFieldsArray.push(indexDef.numberOfAdditionalSources);
        
        if (indexDef.additionalSources) {
            indexDef.additionalSources().forEach(source => {
                checkedFieldsArray.push(source.code);
                checkedFieldsArray.push(source.name);
            });
        }

        const defaultFieldOptions = indexDef.defaultFieldOptions();
        if (defaultFieldOptions)
            addDirtyFlagInput(defaultFieldOptions);

        this.dirtyFlag = new ko.DirtyFlag(checkedFieldsArray, false, jsonUtil.newLineNormalizingHashFunction);

        this.isSaveEnabled = ko.pureComputed(() => {
            const editIndex = this.isEditingExistingIndex();
            const isDirty = this.dirtyFlag().isDirty();

            return !editIndex || isDirty;
        });
    }

    private editExistingIndex(indexName: string) {
        this.originalIndexName = indexName;
        this.termsUrl(appUrl.forTerms(indexName, this.activeDatabase()));
        this.queryUrl(appUrl.forQuery(this.activeDatabase(), indexName));
    }

    addMapHelpPopover() {
        popoverUtils.longWithHover($("#map-title small"),
            {
                content: 'Maps project the fields to search on or to group by. It uses LINQ query syntax.<br/>' +
                'Example:</br><pre><span class="token keyword">from</span> order <span class="token keyword">in</span>' +
                ' docs.Orders<br/><span class="token keyword">where</span> order.IsShipped<br/>' +
                '<span class="token keyword">select new</span><br/>{</br>   order.Date, <br/>   order.Amount,<br/>' +
                '   RegionId = order.Region.Id <br />}</pre>Each map function should project the same set of fields.'
            });
    }

    addReduceHelpPopover() {
        popoverUtils.longWithHover($("#reduce-title small"),
            {
                content: 'The Reduce function consolidates documents from the Maps stage into a smaller set of documents.<br />' +
                'It uses LINQ query syntax.<br/>Example:</br><pre><span class="token keyword">from</span> result ' +
                '<span class="token keyword">in</span> results<br/><span class="token keyword">group</span> result ' +
                '<span class="token keyword">by new</span> { result.RegionId, result.Date } into g<br/>' +
                '<span class="token keyword">select new</span><br/>{<br/>  Date = g.Key.Date,<br/>  ' +
                'RegionId = g.Key.RegionId,<br/>  Amount = g.Sum(x => x.Amount)<br/>}</pre>' +
                'The objects produced by the Reduce function should have the same fields as the inputs.'
            });
    }
    
    addAdditionalSourcesPopover() {
        const html = $("#additional-source-template").html();
        popoverUtils.longWithHover($("#additionalSources small.info"), {
            content: html,
            placement: "top"
        });
    }


    addMap() {
        eventsCollector.default.reportEvent("index", "add-map");
        this.editedIndex().addMap();
    }

    addReduce() {
        eventsCollector.default.reportEvent("index", "add-reduce");
        const editedIndex = this.editedIndex();
        if (!editedIndex.hasReduce()) {
            editedIndex.hasReduce(true);
            editedIndex.reduce("");
            editedIndex.reduce.clearError();
        }
    }

    removeMap(mapIndex: number) {
        eventsCollector.default.reportEvent("index", "remove-map");
        this.editedIndex().maps.splice(mapIndex, 1);
    }

    removeReduce() {
        eventsCollector.default.reportEvent("index", "remove-reduce");
        this.editedIndex().reduce(null);
        this.editedIndex().hasReduce(false);
    }

    addField() {
        eventsCollector.default.reportEvent("index", "add-field");
        this.editedIndex().addField();
    }

    removeField(field: indexFieldOptions) {
        eventsCollector.default.reportEvent("index", "remove-field");
        if (field.isDefaultOptions()) {
            this.editedIndex().removeDefaultFieldOptions();
        } else {
            this.editedIndex().fields.remove(field);
        }
    }

    addDefaultField() {
        eventsCollector.default.reportEvent("index", "add-field");
        this.editedIndex().addDefaultField();
    }

    addConfigurationOption() {
        eventsCollector.default.reportEvent("index", "add-configuration-option");
        this.editedIndex().addConfigurationOption();
    }

    removeConfigurationOption(item: configurationItem) {
        eventsCollector.default.reportEvent("index", "remove-configuration-option");
        this.editedIndex().removeConfigurationOption(item);
    }

    createConfigurationOptionAutocompleter(item: configurationItem) {
        return ko.pureComputed(() => {
            const key = item.key();
            const options = configurationItem.ConfigurationOptions;
            const usedOptions = this.editedIndex().configuration().filter(f => f !== item).map(x => x.key());

            const filteredOptions = _.difference(options, usedOptions);

            if (key) {
                return filteredOptions.filter(x => x.toLowerCase().includes(key.toLowerCase()));
            } else {
                return filteredOptions;
            }
        });
    }

    createFieldNameAutocompleter(field: indexFieldOptions): KnockoutComputed<string[]> {
        return ko.pureComputed(() => {
            const name = field.name();
            const fieldNames = this.fieldNames();
            const otherFieldNames = this.editedIndex().fields().filter(f => f !== field).map(x => x.name());

            const filteredFieldNames = _.difference(fieldNames, otherFieldNames);

            if (name) {
                return filteredFieldNames.filter(x => x.toLowerCase().includes(name.toLowerCase()));
            } else {
                return filteredFieldNames;
            }
        });
    }

    private fetchIndexToEdit(indexName: string): JQueryPromise<Raven.Client.Documents.Indexes.IndexDefinition> {
        return new getIndexDefinitionCommand(indexName, this.activeDatabase())
            .execute()
            .done(result => {

                if (result.Type.startsWith("Auto")) {
                    // Auto Index
                    this.isAutoIndex(true);
                    this.editedIndex(new autoIndexDefinition(result));
                } else {
                    // Regular Index
                    this.editedIndex(new indexDefinition(result));
                }

                this.originalIndexName = this.editedIndex().name();
                this.editedIndex().hasReduce(!!this.editedIndex().reduce());
                this.updateIndexFields();
            });
    }

    private validate(): boolean {
        let valid = true;

        const editedIndex = this.editedIndex();

        if (!this.isValid(this.editedIndex().validationGroup))
            valid = false;

        editedIndex.fields().forEach(field => {
            if (!this.isValid(field.validationGroup)) {
                valid = false;
            }

            if (field.hasSpatialOptions()) {               
                if (!this.isValid(field.spatial().validationGroup)) {
                    valid = false;
                }
            }                      
        });

        editedIndex.maps().forEach(map => {
            if (!this.isValid(map.validationGroup)) {
                valid = false;
            }
        });

        editedIndex.configuration().forEach(config => {
            if (!this.isValid(config.validationGroup)) {
                valid = false;
            }
        });

        return valid;
    }

    save() {
        const editedIndex = this.editedIndex();

        if (!this.validate()) {
            return;
        }

        this.saveInProgress(true);

        //if index name has changed it isn't the same index
        /* TODO
        if (this.originalIndexName === this.indexName() && editedIndex.lockMode === "LockedIgnore") {
            messagePublisher.reportWarning("Can not overwrite locked index: " + editedIndex.name() + ". " + 
                                            "Any changes to the index will be ignored.");
            return;
        }*/

        const indexDto = editedIndex.toDto();

        this.saveIndex(indexDto)
            .always(() => this.saveInProgress(false));
    }

    private saveIndex(indexDto: Raven.Client.Documents.Indexes.IndexDefinition): JQueryPromise<Raven.Client.Documents.Indexes.PutIndexResult> {
        eventsCollector.default.reportEvent("index", "save");

        return new saveIndexDefinitionCommand(indexDto, this.activeDatabase())
            .execute()
            .done(() => {
                this.dirtyFlag().reset();
                this.editedIndex().name.valueHasMutated();
                //TODO: merge suggestion: var isSavingMergedIndex = this.mergeSuggestion() != null;

                if (!this.isEditingExistingIndex()) {
                    this.isEditingExistingIndex(true);
                    this.editExistingIndex(indexDto.Name);
                }
                /* TODO merge suggestion
                if (isSavingMergedIndex) {
                    var indexesToDelete = this.mergeSuggestion().canMerge.filter((indexName: string) => indexName != this.editedIndex().name());
                    this.deleteMergedIndexes(indexesToDelete);
                    this.mergeSuggestion(null);
                }*/

                this.updateUrl(indexDto.Name, false /* TODO isSavingMergedIndex */);
            });
    }

    updateUrl(indexName: string, isSavingMergedIndex: boolean = false) {
        const url = appUrl.forEditIndex(indexName, this.activeDatabase());
        this.navigate(url);
        /* TODO:merged index
        else if (isSavingMergedIndex) {
            super.updateUrl(url);
        }*/
    }

    deleteIndex() {
        eventsCollector.default.reportEvent("index", "delete");
        const indexName = this.originalIndexName;
        if (indexName) {
            const db = this.activeDatabase();
            const deleteViewModel = new deleteIndexesConfirm([indexName], db);
            deleteViewModel.deleteTask.done((can: boolean) => {
                if (can) {
                    this.dirtyFlag().reset(); // Resync Changes
                    router.navigate(appUrl.forIndexes(db));
                }
            });

            dialog.show(deleteViewModel);
        }
    }

    cloneIndex() {
        this.isEditingExistingIndex(false);
        this.editedIndex().name(null);
        this.editedIndex().validationGroup.errors.showAllMessages(false);
    }

    getCSharpCode() {
        eventsCollector.default.reportEvent("index", "generate-csharp-code");
        new getCSharpIndexDefinitionCommand(this.editedIndex().name(), this.activeDatabase())
            .execute()
            .done((data: string) => app.showBootstrapDialog(new showDataDialog("C# Index Definition", data, "csharp")));
    }

    /* TODO
    refreshIndex() {
        eventsCollector.default.reportEvent("index", "refresh");
        var canContinue = this.canContinueIfNotDirty('Unsaved Data', 'You have unsaved data. Are you sure you want to refresh the index from the server?');
        canContinue.done(() => {
            this.fetchIndexData(this.originalIndexName)
                .done(() => {
                    this.initializeDirtyFlag();
                    this.editedIndex().name.valueHasMutated();
            });
        });
    }

    //TODO: copy index
    */
    formatIndex(mapIndex: number) {
        eventsCollector.default.reportEvent("index", "format-index");
        const index: indexDefinition = this.editedIndex();
        const mapToFormat = index.maps()[mapIndex].map;

        this.setFormattedText(mapToFormat);
    }

    formatReduce() {
        eventsCollector.default.reportEvent("index", "format-index");
        const index: indexDefinition = this.editedIndex();

        const reduceToFormat = index.reduce;

        this.setFormattedText(reduceToFormat);
    }

    private setFormattedText(textToFormat: KnockoutObservable<string>) {
        new formatIndexCommand(this.activeDatabase(), textToFormat())
            .execute()
            .done((formatedText) => {
                textToFormat(formatedText.Expression);             
            });
    }

    fileSelected() {
        eventsCollector.default.reportEvent("index", "additional-source");
        const fileInput = <HTMLInputElement>document.querySelector("#additionalSourceFilePicker");
        const self = this;
        if (fileInput.files.length === 0) {
            return;
        }

        const file = fileInput.files[0];
        const fileName = file.name;
        
        const reader = new FileReader();
        reader.onload = function() {
// ReSharper disable once SuspiciousThisUsage
            self.onFileAdded(fileName, this.result);
        };
        reader.onerror = function(error: any) {
            alert(error);
        };
        reader.readAsText(file);

        $("#additionalSourceFilePicker").val(null);
    }
    
    private onFileAdded(fileName: string, contents: string) {
        const sources = this.editedIndex().additionalSources;
        const newItem = additionalSource.create(this.findUniqueNameForAdditionalSource(fileName), contents);
        this.editedIndex().additionalSources.push(newItem);
        this.selectedSourcePreview(newItem);
    }
    
    private findUniqueNameForAdditionalSource(fileName: string) {
        const sources = this.editedIndex().additionalSources;
        const existingItem = sources().find(x => x.name() === fileName);
        if (existingItem) {
            const extensionPosition = fileName.lastIndexOf(".");
            const fileNameWoExtension = fileName.substr(0, extensionPosition - 1);
            
            let idx = 1;
            while (true) {
                const suggestedName = fileNameWoExtension + idx + ".cs";
                if (_.every(sources(), x => x.name() !== suggestedName)) {
                    return suggestedName;
                }
                idx++;
            }
        } else {
            return fileName;
        }
    }

    deleteAdditionalSource(sourceToDelete: additionalSource) {
        if (this.selectedSourcePreview() === sourceToDelete) {
            this.selectedSourcePreview(null);
        }
        this.editedIndex().additionalSources.remove(sourceToDelete);
    }

    previewAdditionalSource(source: additionalSource) {
        this.selectedSourcePreview(source);
    }

    /* TODO

    replaceIndex() {
        eventsCollector.default.reportEvent("index", "replace");
        var indexToReplaceName = this.editedIndex().name();
        var replaceDialog = new replaceIndexDialog(indexToReplaceName, this.activeDatabase());

        replaceDialog.replaceSettingsTask.done((replaceDocument: any) => {
            if (!this.editedIndex().isSideBySideIndex()) {
                this.editedIndex().name(index.SideBySideIndexPrefix + this.editedIndex().name());
            }

            var indexDef = this.editedIndex().toDto();
            var replaceDocumentKey = indexReplaceDocument.replaceDocumentPrefix + this.editedIndex().name();

            this.saveIndex(indexDef)
                .fail((response: JQueryXHR) => messagePublisher.reportError("Failed to save replace index.", response.responseText, response.statusText))
                .done(() => {
                    new saveDocumentCommand(replaceDocumentKey, replaceDocument, this.activeDatabase(), false)
                        .execute()
                        .fail((response: JQueryXHR) => messagePublisher.reportError("Failed to save replace index document.", response.responseText, response.statusText))
                        .done(() => messagePublisher.reportSuccess("Successfully saved side-by-side index"));
                })
                .always(() => dialog.close(replaceDialog));
        });

        app.showBootstrapDialog(replaceDialog);
    }*/

    //TODO: below we have functions for remaining features: 

    /* TODO test index
    makePermanent() {
        eventsCollector.default.reportEvent("index", "make-permanent");
        if (this.editedIndex().name() && this.editedIndex().isTestIndex()) {
            this.editedIndex().isTestIndex(false);
            // trim Test prefix
            var indexToDelete = this.editedIndex().name();
            this.editedIndex().name(this.editedIndex().name().substr(index.TestIndexPrefix.length));
            var indexDef = this.editedIndex().toDto();

            this.saveIndex(indexDef)
                .done(() => {
                    new deleteIndexCommand(indexToDelete, this.activeDatabase()).execute();
                });
        }
    }
*/

    /* TODO side by side

    cancelSideBySideIndex() {
        eventsCollector.default.reportEvent("index", "cancel-side-by-side");
        var indexName = this.originalIndexName;
        if (indexName) {
            var db = this.activeDatabase();
            var cancelSideBySideIndexViewModel = new cancelSideBySizeConfirm([indexName], db);
            cancelSideBySideIndexViewModel.cancelTask.done(() => {
                //prevent asking for unsaved changes
                this.dirtyFlag().reset(); // Resync Changes
                router.navigate(appUrl.forIndexes(db));
            });

            dialog.show(cancelSideBySideIndexViewModel);
        }
    }

    */

    /*TODO merged indexes
   private deleteMergedIndexes(indexesToDelete: string[]) {
       var db = this.activeDatabase();
       var deleteViewModel = new deleteIndexesConfirm(indexesToDelete, db, "Delete Merged Indexes?");
       dialog.show(deleteViewModel);
   }*/

}

export = editIndex;
