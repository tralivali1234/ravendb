// -----------------------------------------------------------------------
//  <copyright file="SubscriptionBatchOptions.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Sparrow.Json;

namespace Raven.Client.Documents.Subscriptions
{
    internal class SubscriptionConnectionClientMessage
    {
        internal enum MessageType
        {
            None,
            Acknowledge,
            DisposedNotification
        }

        public MessageType Type { get; set; }
        public string ChangeVector { get; set; }
    }

    internal class BatchFromServer
    {
        public List<SubscriptionConnectionServerMessage> Messages;
        public IDisposable ReturnContext;
        public List<BlittableJsonReaderObject> Includes;
    }
    internal class SubscriptionConnectionServerMessage
    {
        internal enum MessageType
        {
            None,
            ConnectionStatus,
            EndOfBatch,
            Data,
            Includes,
            Confirm,
            Error
        }

        internal enum ConnectionStatus
        {
            None,
            Accepted,
            InUse,
            Closed,
            NotFound,
            Redirect,
            ForbiddenReadOnly,
            Forbidden,
            Invalid,
            ConcurrencyReconnect
        }

        internal class SubscriptionRedirectData
        {
            public string CurrentTag;
            public string RedirectedTag;
        }

        public MessageType Type { get; set; }
        public ConnectionStatus Status { get; set; }
        public BlittableJsonReaderObject Data { get; set; }

        public BlittableJsonReaderObject Includes { get; set; }
        public string Exception { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Holds subscription connection properties, control both how client and server side behaves   
    /// </summary>
    public class SubscriptionWorkerOptions
    {
        private SubscriptionWorkerOptions()
        {
            // for deserialization
            Strategy = SubscriptionOpeningStrategy.OpenIfFree;
            MaxDocsPerBatch = 4096;
            TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5);
            MaxErroneousPeriod = TimeSpan.FromMinutes(5);
        }

        /// <summary>
        /// Create a subscription connection
        /// </summary>
        /// <param name="subscriptionName">Subscription name as received from CreateSubscription</param>
        public SubscriptionWorkerOptions(string subscriptionName) : this()
        {
            if (string.IsNullOrEmpty(subscriptionName)) 
                throw new ArgumentException("Value cannot be null or empty.", nameof(subscriptionName));
            
            SubscriptionName = subscriptionName;
        }

        /// <summary>
        /// Subscription name as received from CreateSubscription
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local : It does not play nicely with JsonDeserializationBase.GenerateJsonDeserializationRoutine
        public string SubscriptionName { get; private set; }

        /// <summary>
        /// Cooldown time between connection retry. Default: 5 seconds
        /// </summary>
        public TimeSpan TimeToWaitBeforeConnectionRetry { get; set; }

        /// <summary>
        /// Whether subscriber error should halt the subscription processing or not. Default: false
        /// </summary>
        public bool IgnoreSubscriberErrors { get; set; }

        /// <summary>
        /// How connection attempt handle existing\incoming connection. Default: OpenIfFree
        /// </summary>
        public SubscriptionOpeningStrategy Strategy { get; set; }

        /// <summary>
        /// Max amount that the server will try to retrieve and send to client. Default: 4096
        /// </summary>
        public int MaxDocsPerBatch { get; set; }

        /// <summary>
        /// Maximum amount of time during which a subscription connection may be in erroneous state. Default: 5 minutes
        /// </summary>
        public TimeSpan MaxErroneousPeriod { get; set; }

        [Obsolete("Use MaxErroneousPeriod instead")]
        public TimeSpan MaxErrorousPeriod
        {
            get => MaxErroneousPeriod;
            set => MaxErroneousPeriod = value;
        }

        /// <summary>
        /// Will continue the subscription work until the server have no more new documents to send.
        /// That's a useful practice for ad-hoc, one-time, persistent data processing. 
        /// </summary>
        public bool CloseWhenNoDocsLeft { get; set; }
    }
}
