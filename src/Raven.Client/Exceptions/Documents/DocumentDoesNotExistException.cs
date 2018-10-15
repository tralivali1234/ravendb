//-----------------------------------------------------------------------
// <copyright file="DocumentDoesNotExistException.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Raven.Client.Exceptions.Documents
{
    public class DocumentDoesNotExistException : RavenException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentDoesNotExistException"/> class.
        /// </summary>
        public DocumentDoesNotExistException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentDoesNotExistException"/> class.
        /// </summary>
        public DocumentDoesNotExistException(string id) : base($"Document '{id}' does not exist.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentDoesNotExistException"/> class.
        /// </summary>
        public DocumentDoesNotExistException(string id, string message) : base($"Document '{id}' does not exist. {message}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentDoesNotExistException"/> class.
        /// </summary>
        public DocumentDoesNotExistException(string id, Exception inner) : base($"Document '{id}' does not exist.", inner)
        {
        }
    }
}
