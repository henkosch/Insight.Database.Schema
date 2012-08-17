﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using System.Transactions;
using System.Configuration;
using System.Data.SqlClient;
using System.Data;
using System.Data.Common;

namespace Insight.Database.Schema.Tests
{
	[TestFixture]
	public class InstallerTests : BaseInstallerTest
	{
		/// <summary>
		/// The schemas to use for testing.
		/// NOTE: if you create dependencies between items, put them in dependency order.
		/// The Drop test case (and probably others) will use this order to help execute test cases.
		/// </summary>
		public IEnumerable<IEnumerable<string>> Schemas = new List<IEnumerable<string>>()
		{
			// just tables
			new string[] 
			{ 
				@"CREATE TABLE Beer ([ID] [int], Description [varchar](128))",
				@"CREATE TABLE Wine ([ID] [int], Description [varchar](128))",
			},
			// just procs
			new string[] 
			{ 
				@"CREATE PROC TestProc1 AS SELECT 1",
				@"CREATE PROC TestProc2 AS SELECT 2",
			},
			// tables and procs
			new string[] 
			{ 
				@"CREATE TABLE [Beer] ([ID] [int], Description [varchar](128))",
				@"CREATE PROC [BeerProc] AS SELECT * FROM [Beer]",
			},
			// procs with dependencies on other procs and permissions
			new string[] 
			{ 
				@"CREATE PROC TestProc1 AS SELECT 1",
				@"CREATE PROC TestProc2 AS EXEC TestProc1",
				@"GRANT EXEC ON [TestProc1] TO [public]",
			},
			// just tables
			new string[] 
			{ 
				@"CREATE TABLE Beer ([ID] [int], Description [varchar](128))",
				@"GRANT SELECT ON [Beer] TO [public]",
				@"GRANT UPDATE ON [Beer] TO [public]",
			},
			// set of all supported dependencies based on a table
			new string[] 
			{ 
				@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [varchar](128))",
				@"ALTER TABLE [Beer] ADD CONSTRAINT PK_Beer PRIMARY KEY ([ID])",
				@"ALTER TABLE [Beer] ADD CONSTRAINT CK_BeerTable CHECK (ID > 0 OR Description > 'a')",
				@"ALTER TABLE [Beer] ADD CONSTRAINT CK_BeerColumn CHECK (ID > 5)",
				@"ALTER TABLE [Beer] ADD CONSTRAINT DF_Beer_Description DEFAULT 'IPA' FOR Description",

				@"CREATE VIEW BeerView AS SELECT * FROM Beer",
				@"CREATE PROC [BeerProc] AS SELECT * FROM BeerView",
				@"GRANT EXEC ON [BeerProc] TO [public]",

				@"CREATE FUNCTION [BeerFunc] () RETURNS [int] AS BEGIN DECLARE @i [int] SELECT @i=MAX(ID) FROM BeerView RETURN @i END",
				@"CREATE FUNCTION [BeerTableFunc] () RETURNS @IDs TABLE (ID [int]) AS BEGIN INSERT INTO @IDs SELECT ID FROM BeerView RETURN END",

				@"CREATE TABLE Keg ([ID] [int], [BeerID] [int])",
				@"ALTER TABLE [Keg] ADD CONSTRAINT FK_Keg_Beer FOREIGN KEY ([BeerID]) REFERENCES Beer (ID)",

				@"-- AUTOPROC All [Beer]",
			},
			// set of dependencies based on user-defined types
			new string[]
			{
				@"CREATE TYPE BeerName FROM [varchar](256)",
				@"CREATE PROC BeerProc (@Name [BeerName]) AS SELECT @Name",
			},
			// tests around indexes
			new string[]
			{
				@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [varchar](128))",
				@"ALTER TABLE [Beer] ADD CONSTRAINT PK_Beer PRIMARY KEY NONCLUSTERED ([ID])",
				@"CREATE CLUSTERED INDEX [IX_Beer_Description] ON Beer (Description)",
			},
			// xml indexes
			new string[]
			{
				@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [xml])",
				@"ALTER TABLE [Beer] ADD CONSTRAINT PK_Beer PRIMARY KEY CLUSTERED ([ID])",
				@"CREATE PRIMARY XML INDEX IX_Beer_XML ON Beer (Description)",
				@"CREATE XML INDEX IX_Beer_Xml2 ON Beer(Description) USING XML INDEX IX_Beer_Xml FOR PATH",
			},
			// persistent views
			new string[]
			{
				@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [xml])",
				@"-- INDEXEDVIEW
					CREATE VIEW BeerView WITH SCHEMABINDING AS SELECT ID, Description FROM dbo.Beer",
				@"CREATE UNIQUE CLUSTERED INDEX IX_BeerView ON BeerView (ID)",
			},
		};

		#region Install and Drop Tests
		/// <summary>
		/// Run through each of the schemas and make sure that they install properly.
		/// </summary>
		/// <param name="connectionString">The connection string to test against.</param>
		/// <param name="schema">The schema to install.</param>
		[Test]
		public void TestInstallSchemas(
			[ValueSource("ConnectionStrings")] string connectionString,
			[ValueSource("Schemas")] IEnumerable<string> schema)
		{
			TestWithRollback(connectionString, connection =>
			{
				// try to install the schema
				Install(connection, schema);

				// verify that they are there
				VerifyObjectsAndRegistry(schema, connection);
			});
		}

		/// <summary>
		/// Install schemas and then try to drop one object at a time.
		/// </summary>
		/// <param name="connectionString">The connection string to test against.</param>
		/// <param name="schema">The schema to install.</param>
		[Test]
		public void TestDropObjects(
			[ValueSource("ConnectionStrings")] string connectionString,
			[ValueSource("Schemas")] IEnumerable<string> schema)
		{
			TestWithRollback(connectionString, connection =>
			{
				while (schema.Any())
				{
					// try to install the schema and verify that they are there
					Install(connection, schema);
					VerifyObjectsAndRegistry(schema, connection);

					// remove the last object from the schema and try again
					schema = schema.Take(schema.Count() - 1);
				}
			});
		}
		#endregion

		#region Uninstall Tests
		/// <summary>
		/// Make sure that we can uninstall any of the test schemas we are working with.
		/// </summary>
		/// <param name="connectionString">The connection string to test against.</param>
		/// <param name="schema">The schema to uninstall.</param>
		[Test]
		public void TestUninstall(
			[ValueSource("ConnectionStrings")] string connectionString,
			[ValueSource("Schemas")] IEnumerable<string> schema)
		{
			TestWithRollback(connectionString, connection =>
			{
				// try to install the schema and verify that they are there
				Install(connection, schema);
				VerifyObjectsAndRegistry(schema, connection);

				// uninstall it
				SchemaInstaller installer = new SchemaInstaller(connection);
				installer.Uninstall(TestSchemaGroup);

				// make sure the registry is empty
				SchemaRegistry registry = new SchemaRegistry(connection, TestSchemaGroup);
				Assert.IsTrue(!registry.Entries.Any());

				// make sure all of the objects exist in the database
				foreach (var schemaObject in schema.Select(s => new SchemaObject(s)))
					Assert.False(schemaObject.Verify(connection), "Object {0} is not deleted from database", schemaObject.Name);
			});
		}
		#endregion

		#region Modify Tests
		/// <summary>
		/// Make sure that we can modify any of the test schemas we are working with.
		/// </summary>
		/// <param name="connectionString">The connection string to test against.</param>
		/// <param name="schema">The schema to uninstall.</param>
		[Test]
		public void TestModify(
			[ValueSource("ConnectionStrings")] string connectionString,
			[ValueSource("Schemas")] IEnumerable<string> schema)
		{
			TestWithRollback(connectionString, connection =>
			{
				// try to install the schema and verify that they are there
				Install(connection, schema);
				VerifyObjectsAndRegistry(schema, connection);

				// now modify each schema object one at a time
				List<string> modifiedSchema = schema.ToList();
				for (int i = modifiedSchema.Count - 1; i >= 0; i--)
				{
					// modify the schema
					modifiedSchema[i] = modifiedSchema[i] + " -- MODIFIED ";

					// install the modified schema
					Install(connection, modifiedSchema);
					VerifyObjectsAndRegistry(modifiedSchema, connection);
				}
			});
		}
		#endregion

		[Test]
		public void TestTableModify([ValueSource("ConnectionStrings")] string connectionString)
		{
			TestWithRollback(connectionString, connection =>
			{
				var tables = new string[]
				{
					// add columns of various types
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](128))",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256))",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [OriginalGravity][decimal](18,4) NOT NULL)",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [OriginalGravity][decimal](18,4) NOT NULL, [Stuff][xml] NULL)",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [OriginalGravity][decimal](18,4) NOT NULL, [Stuff][xml] NULL, [ChangeDate][rowversion])",

					// drop columns and add them at the same time
					// test identity creation
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [NewID] [int] IDENTITY (10, 10))",

					// modify some types
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [ModifyDecimal] [decimal](18, 0) NOT NULL)",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [ModifyDecimal] [decimal](18, 0) NULL)",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [ModifyDecimal] [decimal](18, 2) NULL)",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [ModifyString] [varchar](32))",
					"CREATE TABLE Beer ([ID] [int] NOT NULL, [Description][varchar](256), [ModifyString] [varchar](MAX))",
				};

				for (int i = 0; i < tables.Length; i++)
				{
					List<string> schema = new List<string>() { tables[i] };
					schema.Add(@"ALTER TABLE [Beer] ADD CONSTRAINT PK_Beer PRIMARY KEY ([ID])");
					schema.Add(@"ALTER TABLE [Beer] ADD CONSTRAINT CK_BeerTable CHECK (ID > 0)");
					schema.Add(@"ALTER TABLE [Beer] ADD CONSTRAINT CK_BeerColumn CHECK (ID > 5)");
					schema.Add(@"ALTER TABLE [Beer] ADD CONSTRAINT DF_Beer_Description DEFAULT 'IPA' FOR Description");
					schema.Add(@"CREATE VIEW BeerView AS SELECT * FROM Beer");
					schema.Add(@"CREATE VIEW BeerView2 WITH SCHEMABINDING AS SELECT ID, Description FROM dbo.Beer");
					schema.Add(@"CREATE NONCLUSTERED INDEX IX_Beer ON Beer (Description)");
					schema.Add(@"CREATE PROC [BeerProc] AS SELECT * FROM BeerView");
					schema.Add(@"GRANT EXEC ON [BeerProc] TO [public]");
					schema.Add(@"CREATE FUNCTION [BeerFunc] () RETURNS [int] AS BEGIN DECLARE @i [int] SELECT @i=MAX(ID) FROM BeerView RETURN @i END");
					schema.Add(@"CREATE FUNCTION [BeerTableFunc] () RETURNS @IDs TABLE (ID [int]) AS BEGIN INSERT INTO @IDs SELECT ID FROM BeerView RETURN END");
					schema.Add(@"CREATE TABLE Keg ([ID] [int], [BeerID] [int])");
					schema.Add(@"ALTER TABLE [Keg] ADD CONSTRAINT FK_Keg_Beer FOREIGN KEY ([BeerID]) REFERENCES Beer (ID)");
					schema.Add(@"-- AUTOPROC All [Beer]");

					// try to install the schema and verify that they are there
					Install(connection, schema);
					VerifyObjectsAndRegistry(schema, connection);
				}
			});
		}

		//[Test]
		//public void ShouldThrowExceptionOnModifyInlineConstraint([ValueSource("ConnectionStrings")] string connectionString,
		//	[Values(
		//		@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [varchar](128) CHECK (Description LIKE '%IPA%'))",
		//		@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [varchar](128) CONSTRAINT PK_Beer PRIMARY KEY ([ID]))",
		//		@"CREATE TABLE Beer ([ID] [int] NOT NULL, Description [varchar](128) DEFAULT ('foo'))"
		//	)] string table)
		//{
		//	TestWithRollback(connectionString, connection =>
		//	{
		//		// try to install the schema and verify that they are there
		//		List<string> schema = new List<string>() { table };
		//		Install(connection, schema);
		//		VerifyObjectsAndRegistry(schema, connection);

		//		// try to install the schema and verify that they are there
		//		schema = new List<string>() { table + " -- MODIFIED" };
		//		Assert.Throws<ApplicationException>(() => Install(connection, schema));
		//	});
		//}
	
		private static void VerifyObjectsAndRegistry(IEnumerable<string> schema, RecordingDbConnection connection)
		{
			connection.DoNotLog(() =>
			{
				// make sure the schema registry was updated
				SchemaRegistry registry = new SchemaRegistry(connection, TestSchemaGroup);

				// make sure all of the objects exist in the database
				foreach (var schemaObject in schema.Select(s => new SchemaObject(s)))
				{
					// azure doesn't support xml index, so lets comment those out
					if (schemaObject.Sql.Contains("XML INDEX") && connection.ConnectionString.Contains("windows.net"))
						continue;

					Assert.True(schemaObject.Verify(connection), "Object {0} is missing from database", schemaObject.Name);
					Assert.True(registry.Contains(schemaObject), "Object {0} is missing from registry", schemaObject.Name);
				}
			});
		}

		private static void Install(DbConnection connection, IEnumerable<string> sql)
		{
			SchemaInstaller installer = new SchemaInstaller(connection);
			SchemaObjectCollection schema = new SchemaObjectCollection();
			if (sql != null)
			{
				foreach (string s in sql)
				{
					// azure doesn't support xml index, so lets comment those out
					if (s.Contains("XML INDEX") && connection.ConnectionString.Contains("windows.net"))
						continue;

					schema.Add(s);
				}
			}

			installer.Install("test", schema);
		}
 	}

	static class InstallerTestExtensions
	{
		public static bool ObjectExists(this IDbConnection connection, string tableName)
		{
			return connection.ExecuteScalarSql<int>("SELECT COUNT(*) FROM sys.objects WHERE name = @TableName", new { TableName = SqlParser.UnformatSqlName(tableName) }) > 0;
		}
	}
}