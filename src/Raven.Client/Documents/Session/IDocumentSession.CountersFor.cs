﻿//-----------------------------------------------------------------------
// <copyright file="IDocumentSession.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------


namespace Raven.Client.Documents.Session
{
    /// <summary>
    /// Provides an access to DocumentSessionCounters API.
    /// </summary>
    public partial interface IDocumentSession
    {

        ISessionDocumentCounters CountersFor(string documentId);

        ISessionDocumentCounters CountersFor(object entity);

    }
}
