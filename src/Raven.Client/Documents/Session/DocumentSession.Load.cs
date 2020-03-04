//-----------------------------------------------------------------------
// <copyright file="DocumentSession.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Client.Documents.Session.Operations;

namespace Raven.Client.Documents.Session
{
    /// <summary>
    /// Implements Unit of Work for accessing the RavenDB server
    /// </summary>
    public partial class DocumentSession
    {
        /// <inheritdoc />
        public T Load<T>(string id)
        {
            if (id == null)
                return default;

            var loadOperation = new LoadOperation(this);
            loadOperation.ById(id);

            var command = loadOperation.CreateRequest();

            if (command != null)
            {
                RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);
                loadOperation.SetResult(command.Result);
            }

            return loadOperation.GetDocument<T>();
        }

        /// <inheritdoc />
        public Dictionary<string, T> Load<T>(IEnumerable<string> ids)
        {
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));

            var loadOperation = new LoadOperation(this);
            LoadInternal(ids.ToArray(), loadOperation);
            return loadOperation.GetDocuments<T>();
        }

        /// <inheritdoc />
        public Dictionary<string, T> LoadInternal<T>(string[] ids, string[] includes)
        {
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));

            var loadOperation = new LoadOperation(this);
            loadOperation.ByIds(ids);
            loadOperation.WithIncludes(includes);

            var command = loadOperation.CreateRequest();
            if (command != null)
            {
                RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);
                loadOperation.SetResult(command.Result);
            }

            return loadOperation.GetDocuments<T>();
        }

        /// <inheritdoc />
        public T[] LoadStartingWith<T>(
            string idPrefix,
            string matches = null,
            int start = 0,
            int pageSize = 25,
            string exclude = null,
            string startAfter = null)
        {
            var loadStartingWithOperation = new LoadStartingWithOperation(this);
            LoadStartingWithInternal(idPrefix, loadStartingWithOperation, null, matches, start, pageSize, exclude, startAfter);
            return loadStartingWithOperation.GetDocuments<T>();
        }

        /// <inheritdoc />
        public void LoadStartingWithIntoStream(
            string idPrefix,
            Stream output,
            string matches = null,
            int start = 0,
            int pageSize = 25,
            string exclude = null,
            string startAfter = null)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            LoadStartingWithInternal(idPrefix, new LoadStartingWithOperation(this), output, matches, start, pageSize, exclude, startAfter);
        }

        /// <inheritdoc />
        public void LoadIntoStream(IEnumerable<string> ids, Stream output)
        {
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            LoadInternal(ids.ToArray(), new LoadOperation(this), output);
        }

        private void LoadStartingWithInternal(
            string idPrefix,
            LoadStartingWithOperation operation,
            Stream stream = null,
            string matches = null,
            int start = 0,
            int pageSize = 25,
            string exclude = null,
            string startAfter = null)
        {
            if (idPrefix == null)
                throw new ArgumentNullException(nameof(idPrefix));

            operation.WithStartWith(idPrefix, matches, start, pageSize, exclude, startAfter);

            var command = operation.CreateRequest();
            if (command != null)
            {
                RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);

                if (stream != null)
                    Context.Write(stream, command.Result.Results.Parent);
                else
                    operation.SetResult(command.Result);
            }
        }

        private void LoadInternal(string[] ids, LoadOperation operation, Stream stream = null)
        {
            if (ids == null) 
                throw new ArgumentNullException(nameof(ids));

            operation.ByIds(ids);

            var command = operation.CreateRequest();
            if (command != null)
            {
                RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);
                if (stream != null)
                    Context.Write(stream, command.Result.Results.Parent);
                else
                    operation.SetResult(command.Result);
            }
        }
    }
}
