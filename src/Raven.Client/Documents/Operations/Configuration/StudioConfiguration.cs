﻿using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations.Configuration
{
    public class StudioConfiguration
    {
        public bool Disabled { get; set; }

        public StudioEnvironment Environment { get; set; }

        public virtual DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(Disabled)] = Disabled,
                [nameof(Environment)] = Environment
            };
        }

        public enum StudioEnvironment
        {
            None,
            Development,
            Testing,
            Production
        }
    }
}
