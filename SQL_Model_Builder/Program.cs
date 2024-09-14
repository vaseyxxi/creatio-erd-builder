using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using Markdig;
using System.Linq;
using Microsoft.Extensions.Configuration;

class Program
{
	static void Main(string[] args)
	{
		// Загрузка настроек из appsettings.json
		IConfiguration config = new ConfigurationBuilder()
			.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
			.Build();

		// Получение настроек базы данных
		string connectionString = config.GetSection("DatabaseSettings:ConnectionString").Value;

		// Получение настроек диаграммы
		var diagramSettings = config.GetSection("DiagramSettings");
		var rootEntityConfigs = config.GetSection("DiagramSettings:RootEntities").Get<List<RootEntityConfig>>(); // Исправлено
		List<string> includedTables = config.GetSection("DiagramSettings:IncludedTables").Get<List<string>>(); // Исправлено
		int maxDepth = diagramSettings.GetValue<int>("MaxDepth");

		// Подключение к базе данных
		using (SqlConnection connection = new SqlConnection(connectionString))
		{
			connection.Open();

			// Получение списка таблиц
			List<Table> tables = GetTables(connection);

			// Получение списка связей между таблицами
			List<Relationship> relationships = GetRelationships(connection);

			// Генерация Markdown документации
			string markdown = GenerateMarkdown(tables, relationships, includedTables, rootEntityConfigs, maxDepth);
			File.WriteAllText("schema.md", markdown);
			Console.WriteLine(markdown);
		}
	}

	static List<Table> GetTables(SqlConnection connection)
	{
		List<Table> tables = new List<Table>();

		// SQL запрос для получения информации о таблицах, колонках и расширенных свойствах
		string sql = @"
            SELECT 
                t.name AS TableName,
                c.name AS ColumnName,
                ty.name AS DataType,
                c.max_length AS MaxLength,
                c.is_nullable AS IsNullable,
				REPLACE((
					select top 1 Name
					from dbo.splitstring(convert(nvarchar(max),ep.value))
					where Name like 'ru-RU%'
				), 'ru-RU|', '') AS [Value]
            FROM sys.tables t
            INNER JOIN sys.columns c ON t.object_id = c.object_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.extended_properties ep ON c.object_id = ep.major_id AND c.column_id = ep.minor_id AND ep.name = 'TS.EntitySchemaColumn.Caption'
            ORDER BY t.name, c.column_id;";

		using (SqlCommand command = new SqlCommand(sql, connection))
		{
			using (SqlDataReader reader = command.ExecuteReader())
			{
				Table currentTable = null;
				while (reader.Read())
				{
					string tableName = reader.GetString(0);
					if (currentTable == null || currentTable.Name != tableName)
					{
						currentTable = new Table { Name = tableName };
						tables.Add(currentTable);
					}

					currentTable.Columns.Add(new Column
					{
						Name = reader.GetString(1),
						DataType = reader.GetString(2),
						MaxLength = reader.GetInt16(3),
						IsNullable = reader.GetBoolean(4),
						Description = reader.IsDBNull(5) ? "" : reader.GetString(5)
					});
				}
			}
		}

		return tables;
	}

	static List<Relationship> GetRelationships(SqlConnection connection)
	{
		// SQL запрос для получения информации о связях
		string sql = @"
            SELECT 
				parent.name AS ParentTable,
				fk.name AS ForeignKeyName,
				child.name AS ChildTable,
				col.name AS RefColName
			FROM sys.foreign_keys fk
			INNER JOIN sys.tables parent ON fk.parent_object_id = parent.object_id
			INNER JOIN sys.tables child ON fk.referenced_object_id = child.object_id
			INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
			INNER JOIN sys.columns col ON fkc.parent_column_id = col.column_id AND fkc.parent_object_id = col.object_id;";

		List<Relationship> relationships = new List<Relationship>();
		using (SqlCommand command = new SqlCommand(sql, connection))
		{
			using (SqlDataReader reader = command.ExecuteReader())
			{
				while (reader.Read())
				{
					relationships.Add(new Relationship
					{
						ParentTable = reader.GetString(0),
						ForeignKeyName = reader.GetString(1),
						ChildTable = reader.GetString(2),
						RefColName = reader.GetString(3)
					});
				}
			}
		}

		return relationships;
	}

	static string GenerateMarkdown(List<Table> tables, List<Relationship> relationships,
									List<string> includedTables,
									List<RootEntityConfig> rootEntityConfigs,
									int maxDepth)
	{
		MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
		string markdown = "# Документация по базе данных\n\n";

		// Описание таблиц
		foreach (Table table in tables.Where(t => includedTables.Contains(t.Name)))
		{
			markdown += $"## Таблица: {table.Name}\n\n";
			markdown += "| Имя колонки | Тип данных | Размер | Допускает NULL | Описание |\n";
			markdown += "|---|---|---|---|---| \n";
			foreach (Column column in table.Columns)
			{
				markdown += $"| {column.Name} | {column.DataType} | {column.MaxLength} | {column.IsNullable} | {column.Description} |\n";
			}
			markdown += "\n";
		}

		// Визуализация связей
		markdown += "## Диаграмма связей\n\n";

		foreach (var rootEntityConfig in rootEntityConfigs)
		{
			markdown += GenerateEntityDiagram(rootEntityConfig.Name, tables, relationships, includedTables,
												rootEntityConfig.Exclusions, maxDepth, rootEntityConfigs);
		}


		return markdown; //Markdown.ToHtml(markdown, pipeline);
	}

	static string GenerateEntityDiagram(string rootEntity, List<Table> tables,
									 List<Relationship> relationships, List<string> excludedTables,
									 List<string> entityExclusions, int maxDepth, List<RootEntityConfig> rootEntityConfigs)
	{
		string diagram = $"### Диаграмма для сущности: {rootEntity}\n\n";
		diagram += "```mermaid\nerDiagram\n";

		// Набор для отслеживания уже добавленных сущностей
		List<string> visitedEntities = new List<string>();
		List<string> describedTables = [.. rootEntityConfigs.Where(r => r.Name != rootEntity).Select(r => r.Name)];
		// Очередь для обхода графа связей
		Queue<(string, int)> entitiesToProcess = new Queue<(string, int)>();
		entitiesToProcess.Enqueue((rootEntity, 0));

		while (entitiesToProcess.Count > 0)
		{
			(string currentEntity, int currentDepth) = entitiesToProcess.Dequeue();
			if (currentDepth >= maxDepth)
				continue;

			// Поиск всех отношений, связанных с текущей сущностью
			foreach (Relationship relationship in relationships.Where(r =>
				(r.ParentTable == currentEntity || r.ChildTable == currentEntity) &&
				excludedTables.Contains(r.ParentTable) && excludedTables.Contains(r.ChildTable)))
			{
				string relatedEntity = (relationship.ParentTable == currentEntity)
					? relationship.ChildTable : relationship.ParentTable;
				// Пропускаем сущность, если она в списке исключений
				if (entityExclusions != null && entityExclusions.Contains(relatedEntity))
					continue;

				// Пропускаем связь, если это связь другой корневой сущности 
				if (relationship.ChildTable != rootEntity && rootEntityConfigs.Any(c => c.Name == relationship.ParentTable && c.Name != rootEntity))
					continue;

				// Пропускаем связь, если это связь другой корневой сущности 
				if (relationship.ParentTable != rootEntity && rootEntityConfigs.Any(c => c.Name == relationship.ChildTable && c.Name != rootEntity))
					continue;

				// Добавление связи на диаграмму, если обе сущности не исключены
				if (!visitedEntities.Contains(relatedEntity))
				{
					diagram += $"    {relationship.ParentTable} ||--|{{{relationship.ChildTable} : {relationship.RefColName} \n";
					diagram += AddTableDescription(relationship.ParentTable, tables, describedTables);
					diagram += AddTableDescription(relationship.ChildTable, tables, describedTables);

					visitedEntities.Add(relatedEntity);
					entitiesToProcess.Enqueue((relatedEntity, currentDepth + 1));
				}
			}
		}

		diagram += "```\n\n";
		return diagram;
	}


	static string AddTableDescription(string name, List<Table> tables, List<string> describedEntities)
	{
		var result = "";
		var cols = tables.FirstOrDefault(t => t.Name == name)?.Columns;
		if (cols != null && !describedEntities.Contains(name))
		{
			result += $"    {name} {{ \n";
			foreach (var item in cols)
			{
				if (item.Name != "Id" && item.Name != "CreatedOn" && item.Name != "CreatedById" && item.Name != "ModifiedOn" && item.Name != "ModifiedById" && item.Name != "ProcessListeners")
				{
					result += $"        {item.DataType} {item.Name} \"{item.Description}\" \n";
				}
			}
			result += $"    }} \n";

		}
		describedEntities.Add(name);

		return result;
	}


	// Класс для конфигурации корневой сущности
	public class RootEntityConfig
	{
		public string Name { get; set; }
		public List<string> Exclusions { get; set; }
	}

	// Класс для представления таблицы
	public class Table
	{
		public string Name { get; set; }
		public List<Column> Columns { get; set; } = new List<Column>();
	}

	// Класс для представления колонки
	public class Column
	{
		public string Name { get; set; }
		public string DataType { get; set; }
		public int MaxLength { get; set; }
		public bool IsNullable { get; set; }
		public string Description { get; set; }
	}

	// Класс для представления связи между таблицами
	public class Relationship
	{
		public string ParentTable { get; set; }
		public string ForeignKeyName { get; set; }
		public string RefColName { get; set; }
		public string ChildTable { get; set; }
	}
}
