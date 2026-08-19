using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Read operations against the AdventureWorks sample schema. Used only when probing found
/// every table they reference.
///
/// The v1 equivalents were capped at <c>TOP (batchSize * 100)</c> with <c>ORDER BY NEWID()</c>,
/// which sorts the entire table on every execution. That is a resource burner wearing an
/// OLTP costume: it made every "normal" read behave like a chaos query and put the cost in
/// the sort rather than in anything the operator chose. These use bound parameters and real
/// predicates, with the deliberate scans kept in the chaos catalogue where they belong.
/// </summary>
public static class AdventureWorksOperations
{
    public static IEnumerable<WorkloadOperation> Reads() =>
    [
        new WorkloadOperation
        {
            Name = "Order lookup",
            Kind = OperationKind.Read,
            Explanation = "Index seek on a single order and its lines — the classic OLTP read.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT soh.SalesOrderID, soh.OrderDate, soh.TotalDue,
                           sod.ProductID, sod.OrderQty, sod.LineTotal
                    FROM Sales.SalesOrderHeader soh
                    JOIN Sales.SalesOrderDetail sod ON sod.SalesOrderID = soh.SalesOrderID
                    WHERE soh.SalesOrderID = @orderId
                    """,
                Parameters = [new SqlParameterValue("@orderId", context.Random.Next(43659, 75124))],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "Order date range",
            Kind = OperationKind.Read,
            Explanation = "Range scan over a date window — the shape most reports take.",
            Build = context =>
            {
                var start = new DateTime(2011, 5, 31).AddDays(context.Random.Next(0, 1000));
                return new SqlRequest
                {
                    Sql = """
                        SELECT TOP (@top) SalesOrderID, OrderDate, CustomerID, TotalDue
                        FROM Sales.SalesOrderHeader
                        WHERE OrderDate >= @start AND OrderDate < @end
                        ORDER BY OrderDate
                        """,
                    Parameters =
                    [
                        new SqlParameterValue("@top", Math.Min(context.MaxRows, 500)),
                        new SqlParameterValue("@start", start),
                        new SqlParameterValue("@end", start.AddDays(30)),
                    ],
                    TimeoutSeconds = context.Timeout,
                    MaxRows = context.MaxRows,
                };
            },
        },

        new WorkloadOperation
        {
            Name = "Customer order history",
            Kind = OperationKind.Read,
            Explanation = "Four-table join from customer to product — join order and lookups.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT TOP (@top) c.CustomerID, p.Name, soh.OrderDate, sod.LineTotal
                    FROM Sales.Customer c
                    JOIN Sales.SalesOrderHeader soh ON soh.CustomerID = c.CustomerID
                    JOIN Sales.SalesOrderDetail sod ON sod.SalesOrderID = soh.SalesOrderID
                    JOIN Production.Product p ON p.ProductID = sod.ProductID
                    WHERE c.CustomerID = @customerId
                    ORDER BY soh.OrderDate DESC
                    """,
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, 500)),
                    new SqlParameterValue("@customerId", context.Random.Next(11000, 30119)),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "Product sales aggregate",
            Kind = OperationKind.Read,
            Explanation = "Grouped aggregation across the detail table — CPU and memory grant.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT TOP (@top) p.Name,
                           SUM(sod.LineTotal) AS Total,
                           AVG(sod.UnitPrice) AS AvgPrice,
                           COUNT_BIG(*)       AS Lines
                    FROM Production.Product p
                    JOIN Sales.SalesOrderDetail sod ON sod.ProductID = p.ProductID
                    GROUP BY p.Name
                    HAVING SUM(sod.LineTotal) > @threshold
                    ORDER BY Total DESC
                    """,
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, 200)),
                    new SqlParameterValue("@threshold", context.Random.Next(100, 100_000)),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "Person name search",
            Kind = OperationKind.Read,
            Explanation = "Leading-wildcard search — forces a scan no index can help with.",
            Build = context =>
            {
                const string Letters = "abcdefghijklmnopqrstuvwxyz";
                var fragment = $"{Letters[context.Random.Next(Letters.Length)]}{Letters[context.Random.Next(Letters.Length)]}";
                return new SqlRequest
                {
                    Sql = """
                        SELECT TOP (@top) BusinessEntityID, FirstName, LastName, ModifiedDate
                        FROM Person.Person
                        WHERE LastName LIKE @pattern
                        ORDER BY ModifiedDate DESC
                        """,
                    Parameters =
                    [
                        new SqlParameterValue("@top", Math.Min(context.MaxRows, 500)),
                        new SqlParameterValue("@pattern", "%" + fragment + "%"),
                    ],
                    TimeoutSeconds = context.Timeout,
                    MaxRows = context.MaxRows,
                };
            },
        },

        new WorkloadOperation
        {
            Name = "Windowed ranking",
            Kind = OperationKind.Read,
            Explanation = "Window functions over a partition — sorts and spools.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT TOP (@top) SalesOrderID, ProductID, LineTotal,
                           ROW_NUMBER() OVER (PARTITION BY ProductID ORDER BY LineTotal DESC) AS RowNum,
                           SUM(LineTotal) OVER (PARTITION BY ProductID) AS TotalPerProduct
                    FROM Sales.SalesOrderDetail
                    WHERE ProductID BETWEEN @from AND @to
                    """,
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, 2000)),
                    new SqlParameterValue("@from", context.Random.Next(707, 990)),
                    new SqlParameterValue("@to", 999),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },
    ];
}
