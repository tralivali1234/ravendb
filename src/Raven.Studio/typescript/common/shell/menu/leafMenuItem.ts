﻿/// <reference path="../../../../typings/tsd.d.ts" />
import generalUtils = require("common/generalUtils");
import intermediateMenuItem = require("common/shell/menu/intermediateMenuItem");

class leafMenuItem implements menuItem {
    title: string;
    tooltip: string;
    nav: boolean | KnockoutObservable<boolean>;
    route: string | Array<string>;
    moduleId: string;
    hash: string;
    dynamicHash: dynamicHashType;
    css: string;
    openAsDialog: boolean;
    path: KnockoutComputed<string>;
    parent: KnockoutObservable<intermediateMenuItem> = ko.observable(null);
    enabled: KnockoutObservable<boolean>;
    type: menuItemType = "leaf";
    itemRouteToHighlight: string;
    alias: boolean;
    
    badgeData: KnockoutObservable<number>;
    countPrefix: KnockoutComputed<string>;
    sizeClass: KnockoutComputed<string>;

    constructor({ title, tooltip, route, moduleId, nav, hash, css, dynamicHash, enabled, openAsDialog, itemRouteToHighlight, badgeData, alias }: {
        title: string,
        route: string | Array<string>,
        moduleId: string,
        nav: boolean | KnockoutObservable<boolean>,
        tooltip?: string,
        hash?: string,
        dynamicHash?: dynamicHashType,
        css?: string,
        openAsDialog?: boolean,
        enabled?: KnockoutObservable<boolean>;
        itemRouteToHighlight?: string;
        badgeData?: KnockoutObservable<number>;
        alias?: boolean;
    }) {
        if (nav && !hash && !dynamicHash && !openAsDialog) {
            console.error("Invalid route configuration:" + title);
        }

        this.badgeData = badgeData || ko.observable<number>();
        this.itemRouteToHighlight = itemRouteToHighlight;
        this.title = title;
        this.route = route;
        this.moduleId = moduleId;
        this.nav = nav;
        this.hash = hash;
        this.dynamicHash = dynamicHash;
        this.css = css;
        this.enabled = enabled;
        this.openAsDialog = openAsDialog;
        this.alias = alias || false;

        this.path = ko.pureComputed(() => {
            if (this.hash) {
                return this.hash;
            } else if (this.dynamicHash) {
                return this.dynamicHash();
            }

            return null;
        });

        this.sizeClass = ko.pureComputed(() => {
            if (!this.badgeData) {
                return "";
            }

            return generalUtils.getSizeClass(this.badgeData());
        });

        this.countPrefix = ko.pureComputed(() => {
            if (!this.badgeData) {
                return null;
            }

            return generalUtils.getCountPrefix(this.badgeData());
        });
    }
}

export = leafMenuItem;
