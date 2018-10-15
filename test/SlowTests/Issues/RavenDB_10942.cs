﻿using System.IO;
using System.Text;
using FastTests;
using Sparrow.Json;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_10942 : RavenTestBase
    {
        [Fact]
        public void ShouldWork()
        {
            using (var context = JsonOperationContext.ShortTermSingleUse())
            {
                const string input = "6ef25cb9-6215-41a8-a80e-218afbf3aa32|d|signature-6ef25cb9-6215-41a8-a80e-218afbf3aa32.png|vrqUT1cMggKAgkw0zWiI8IKAoHIjZEZZIzsDpo7+ajc=|image/png";
                var data = $"{{ 'Value' : '{input}' }}";

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(data)))
                {
                    var json = context.Read(stream, "json");

                    Assert.True(json.TryGet("Value", out LazyStringValue str));
                    Assert.NotNull(str);
                    Assert.Equal(input, str.ToString());
                }
            }
        }
    }
}
