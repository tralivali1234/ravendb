﻿using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Operations.Indexes;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_11683 : RavenTestBase
    {
        [Fact]
        public void AutoMapReduceIndexShouldNotErrorIfCompositeGroupByKeyIsEmptyCollection()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Remittance()
                    {
                        ClaimPayment = new ClaimPayment()
                        {
                            ClaimStatusCode = 4,
                            ServiceLinePayments = new List<ServiceLinePayments>()
                        }
                    });

                    session.SaveChanges();

                    session.Advanced.RawQuery<dynamic>(@"from Remittances as r

group by r.ClaimPayment.ServiceLinePayments[].ClaimAdjustments[].ReasonCode, r.ClaimPayment.ClaimStatusCode
order by count() desc
select r.ClaimPayment.ServiceLinePayments[].ClaimAdjustments[].ReasonCode, r.ClaimPayment.ClaimStatusCode, count()")
                        .WaitForNonStaleResults().ToList();

                    var indexErrors = store.Maintenance.Send(new GetIndexErrorsOperation());

                    Assert.Empty(indexErrors[0].Errors);
                }
            }
        }

        private class ClaimAdjustments
        {
            public int AdjustmentAmount { get; set; }
            public string GroupCode { get; set; }
            public int Quantity { get; set; }
            public int ReasonCode { get; set; }
        }

        private class ServiceLinePayments
        {
            public List<ClaimAdjustments> ClaimAdjustments { get; set; }
        }

        private class ClaimPayment
        {
            public int ClaimStatusCode { get; set; }
            public List<ServiceLinePayments> ServiceLinePayments { get; set; }
        }

        private class Remittance
        {
            public ClaimPayment ClaimPayment { get; set; }
        }
    }
}
