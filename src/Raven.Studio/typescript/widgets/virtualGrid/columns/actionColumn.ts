﻿/// <reference path="../../../../typings/tsd.d.ts"/>

import virtualColumn = require("widgets/virtualGrid/columns/virtualColumn");
import virtualRow = require("widgets/virtualGrid/virtualRow");
import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import utils = require("widgets/virtualGrid/virtualGridUtils");

type actionColumnOpts<T> = {
    extraClass?: (item: T) => string;
    title?: (item:T) => string;
}
type provider<T> = (item: T) => string;

class actionColumn<T> implements virtualColumn {
    private readonly action: (obj: T, idx: number) => void;

    protected gridController: virtualGridController<T>;

    header: string;
    private buttonText: (item: T) => string;
    width: string;
    extraClass: string;

    private opts: actionColumnOpts<T>;

    actionUniqueId = _.uniqueId("action-");

    constructor(gridController: virtualGridController<T>, action: (obj: T, idx: number) => void, header: string, buttonText: provider<T> | string, width: string, opts: actionColumnOpts<T> = {}) {
        this.gridController = gridController;
        this.action = action;
        this.header = header;
        this.buttonText = _.isString(buttonText) ? (item: T) => buttonText : buttonText;
        this.width = width;
        this.opts = opts || {};
    }
    
    get headerTitle() {
        return this.header;
    }

    get headerAsText() {
        return this.header;
    }

    canHandle(actionId: string) {
        return this.actionUniqueId === actionId;
    }

    handle(row: virtualRow) {
        this.action(row.data as T, row.index);
    }

    renderCell(item: T, isSelected: boolean): string {
        const extraButtonHtml = this.opts.title ? ` title="${utils.escape(this.opts.title(item))}" ` : '';
        const extraCssClasses = this.opts.extraClass ? this.opts.extraClass(item) : '';
        return `<div class="cell action-cell" style="width: ${this.width}">
            <button type="button" ${extraButtonHtml} data-action="${this.actionUniqueId}" class="btn btn-sm btn-block ${extraCssClasses}">${this.buttonText(item)}</button>
        </div>`;
    }

}

export = actionColumn;
