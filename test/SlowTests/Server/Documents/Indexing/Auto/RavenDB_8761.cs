﻿using System.Collections.Generic;
using System.Linq;
using FastTests;
using FastTests.Server.Basic.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Server.Documents.Indexing.Auto
{
    public class RavenDB_8761 : RavenTestBase
    {
        [Fact]
        public void Can_group_by_array_values()
        {
            using (var store = GetDocumentStore())
            {
                PutDocs(store);

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                        from Orders group by Lines[].Product
                        order by count()
                        select key() as ProductName, count()")
                            .WaitForNonStaleResults(),

                        // linq
                        session.Query<Order>().GroupByArrayValues(x => x.Lines.Select(y => y.Product)).Select(x => new ProductCount
                        {
                            Count = x.Count(),
                            ProductName = x.Key
                        }),

                        // document query
                        session.Advanced.DocumentQuery<Order>().GroupBy("Lines[].Product").SelectKey(projectedName: "ProductName").SelectCount().OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(2, products.Count);

                        Assert.Equal("products/1", products[0].ProductName);
                        Assert.Equal(1, products[0].Count);

                        Assert.Equal("products/2", products[1].ProductName);
                        Assert.Equal(2, products[1].Count);
                    }
                }

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                            from Orders 
                            group by Lines[].Product, ShipTo.Country 
                            order by count() 
                            select Lines[].Product as ProductName, ShipTo.Country as Country, count()")
                            .WaitForNonStaleResults(),

                        session.Advanced.DocumentQuery<Order>()
                            .GroupBy("Lines[].Product", "ShipTo.Country")
                            .SelectKey("Lines[].Product", "ProductName")
                            .SelectKey("ShipTo.Country", "Country")
                            .SelectCount()
                            .OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(2, products.Count);

                        Assert.Equal("products/1", products[0].ProductName);
                        Assert.Equal(1, products[0].Count);
                        Assert.Equal("USA", products[0].Country);

                        Assert.Equal("products/2", products[1].ProductName);
                        Assert.Equal(2, products[1].Count);
                        Assert.Equal("USA", products[1].Country);
                    }
                }

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                            from Orders 
                            group by Lines[].Product, Lines[].Quantity 
                            order by Lines[].Quantity
                            select Lines[].Product as ProductName, Lines[].Quantity as Quantity, count()")
                            .WaitForNonStaleResults(),

                        // linq
                        session.Query<Order>().GroupByArrayValues(x => x.Lines.Select(y => new { y.Product, y.Quantity })).Select(x => new ProductCount
                        {
                            Count = x.Count(),
                            ProductName = x.Key.Product,
                            Quantity = x.Key.Quantity
                        }),

                        // document query

                        session.Advanced.DocumentQuery<Order>()
                            .GroupBy("Lines[].Product", "Lines[].Quantity")
                            .SelectKey("Lines[].Product", "ProductName")
                            .SelectKey("Lines[].Quantity", "Quantity")
                            .SelectCount()
                            .OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(3, products.Count);

                        Assert.Equal("products/1", products[0].ProductName);
                        Assert.Equal(1, products[0].Count);
                        Assert.Equal(1, products[0].Quantity);

                        Assert.Equal("products/2", products[1].ProductName);
                        Assert.Equal(1, products[1].Count);
                        Assert.Equal(2, products[1].Quantity);

                        Assert.Equal("products/2", products[2].ProductName);
                        Assert.Equal(1, products[2].Count);
                        Assert.Equal(3, products[2].Quantity);
                    }
                }
            }
        }

        [Fact]
        public void Can_group_by_array_content()
        {
            using (var store = GetDocumentStore())
            {
                PutDocs(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Order
                    {
                        Lines = new List<OrderLine>
                        {
                            new OrderLine
                            {
                                Product = "products/1",
                                Quantity = 1
                            },
                            new OrderLine
                            {
                                Product = "products/2",
                                Quantity = 2
                            }
                        },
                        ShipTo = new Address()
                        {
                            Country = "USA"
                        }
                    });

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                        from Orders group by array(Lines[].Product)
                        order by count()
                        select key() as Products, count()")
                            .WaitForNonStaleResults(),

                        // linq
                        session.Query<Order>().GroupByArrayContent(x => x.Lines.Select(y => y.Product)).Select(x => new ProductCount
                        {
                            Count = x.Count(),
                            Products = x.Key
                        }).OrderBy(x => x.Count),

                        // document query
                        session.Advanced.DocumentQuery<Order>().GroupBy(("Lines[].Product", GroupByMethod.Array)).SelectKey(projectedName: "Products").SelectCount().OrderBy("Count").OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(2, products.Count);

                        Assert.Equal(new[] { "products/2" }, products[0].Products);
                        Assert.Equal(1, products[0].Count);

                        Assert.Equal(new[] { "products/1", "products/2" }, products[1].Products);
                        Assert.Equal(2, products[1].Count);
                    }
                }

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                            from Orders 
                            group by array(Lines[].Product), ShipTo.Country 
                            order by count() 
                            select Lines[].Product as Products, ShipTo.Country as Country, count()")
                            .WaitForNonStaleResults(),

                        session.Advanced.DocumentQuery<Order>()
                            .GroupBy(("Lines[].Product", GroupByMethod.Array), ("ShipTo.Country", GroupByMethod.None))
                            .SelectKey("Lines[].Product", "Products")
                            .SelectKey("ShipTo.Country", "Country")
                            .SelectCount()
                            .OrderBy("Count")
                            .OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(2, products.Count);

                        Assert.Equal(new[] { "products/2" }, products[0].Products);
                        Assert.Equal(1, products[0].Count);

                        Assert.Equal(new[] { "products/1", "products/2" }, products[1].Products);
                        Assert.Equal(2, products[1].Count);
                    }
                }

                using (var session = store.OpenSession())
                {
                    foreach (var query in new IEnumerable<ProductCount>[]
                    {
                        // raw query
                        session.Advanced.RawQuery<ProductCount>(
                                @"
                            from Orders 
                            group by array(Lines[].Product), array(Lines[].Quantity) 
                            order by count()
                            select Lines[].Product as Products, Lines[].Quantity as Quantities, count()")
                            .WaitForNonStaleResults(),

                        // document query

                        session.Advanced.DocumentQuery<Order>()
                            .GroupBy(("Lines[].Product", GroupByMethod.Array), ("Lines[].Quantity", GroupByMethod.Array))
                            .SelectKey("Lines[].Product", "Products")
                            .SelectKey("Lines[].Quantity", "Quantities")
                            .SelectCount()
                            .OrderBy("Count")
                            .OfType<ProductCount>()
                    })
                    {
                        var products = query.ToList();

                        Assert.Equal(2, products.Count);

                        Assert.Equal(new [] { "products/2" }, products[0].Products);
                        Assert.Equal(new [] { 3 }, products[0].Quantities);
                        Assert.Equal(1, products[0].Count);

                        Assert.Equal(new [] { "products/1", "products/2" }, products[1].Products);
                        Assert.Equal(new [] { 1, 2 }, products[1].Quantities);
                        Assert.Equal(2, products[1].Count);
                    }
                }
            }
        }

        private class ProductCount
        {
            public string ProductName { get; set; }

            public int Count { get; set; }

            public string Country { get; set; }

            public int Quantity { get; set; }

            public IEnumerable<string> Products { get; set; }

            public IEnumerable<int> Quantities { get; set; }
        }

        private void PutDocs(IDocumentStore store)
        {
            using (var session = store.OpenSession())
            {
                session.Store(new Order
                {
                    Lines = new List<OrderLine>
                    {
                        new OrderLine
                        {
                            Product = "products/1",
                            Quantity = 1
                        },
                        new OrderLine
                        {
                            Product = "products/2",
                            Quantity = 2
                        }
                    },
                    ShipTo = new Address()
                    {
                        Country = "USA"
                    }
                });

                session.Store(new Order
                {
                    Lines = new List<OrderLine>
                    {
                        new OrderLine
                        {
                            Product = "products/2",
                            Quantity = 3
                        }
                    },
                    ShipTo = new Address()
                    {
                        Country = "USA"
                    }
                });

                session.SaveChanges();
            }
        }
    }
}
