﻿using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Queries;
using Xunit;

namespace SlowTests.MailingList.Rusty
{
    public class ProjectionNestedLoadNullPropertyTests : RavenTestBase
    {
        [Fact]
        public void Projection_Returns_Null_For_Nested_Property()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    var config = new PaymentConfig { Currency = "USD" };
                    session.Store( config, PaymentConfig.Id );

                    var order = new Order
                    {
                        CustomerName = "Bob",
                        OrderItems = new[]
                        {
                            new OrderItem { ItemId = "1", Price = 100 },
                            new OrderItem { ItemId = "2", Price = 50 }
                        }
                    };
                    session.Store( order );

                    session.SaveChanges();
                }

                WaitForIndexing( store );

                using (var session = store.OpenSession())
                {
                    var st = (from o in session.Query<Order>()
                        let config = RavenQuery.Load<PaymentConfig>(PaymentConfig.Id)
                        select new OrderProjection
                        {
                            OrderId = o.Id,
                            CustomerName = o.CustomerName,
                            OrderItems = from i in o.OrderItems
                                select new OrderItemProjection
                                {
                                    ItemId = i.ItemId,
                                    Price = i.Price,
                                    Currency = config.Currency
                                }
                        }).ToString();
                    
                    var result = ( from o in session.Query<Order>()
                        let config = RavenQuery.Load<PaymentConfig>( PaymentConfig.Id )
                        select new OrderProjection
                        {
                            OrderId = o.Id,
                            CustomerName = o.CustomerName,
                            OrderItems = from i in o.OrderItems
                                select new OrderItemProjection
                                {
                                    ItemId = i.ItemId,
                                    Price = i.Price,
                                    Currency = config.Currency
                                }
                        } ).Single();
WaitForUserToContinueTheTest(store);
                    Assert.All( result.OrderItems, item => Assert.Equal( "USD", item.Currency ) );
                }
            }
        }

        public class Order
        {
            public string Id { get; set; }
            public string CustomerName { get; set; }
            public IList<OrderItem> OrderItems { get; set; }
        }

        public class OrderItem
        {
            public string ItemId { get; set; }
            public decimal Price { get; set; }
        }

        public class PaymentConfig
        {
            public const string Id = "Config/Payment";
            public string Currency { get; set; }
        }

        public class OrderProjection
        {
            public string OrderId { get; set; }
            public string CustomerName { get; set; }
            public IEnumerable<OrderItemProjection> OrderItems { get; set; }
        }

        public class OrderItemProjection
        {
            public string ItemId { get; set; }
            public decimal Price { get; set; }
            public string Currency { get; set; }
        }
    }
}
