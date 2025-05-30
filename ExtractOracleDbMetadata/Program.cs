using Oracle.ManagedDataAccess.Client;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ExtractOracleDbMetadata
{
    internal partial class Program
    {
        private static readonly string schemasQuery = @"
--SELECT *
SELECT
  XMLELEMENT(""Schemas"", 
      XMLAGG(
          XMLELEMENT(""Schema"",
              XMLFOREST(OWNER AS ""SchemaName"")
          )
      )
  )
AS XML_RESULT
FROM (
    -- List of schemas accessible to current USER.
    WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
    )
    SELECT * FROM user_schemas
)
";

        private static readonly string typesQuery = @"
--SELECT t1.TYPE_OWNER, t1.TYPE_NAME, t1.ATTR_TYPE, t1.ATTR_NAME, t1.ATTR_NO, t1.ATTR_TYPE_OWNER, t1.ATTR_TYPE_NAME, t1.LENGTH, t1.PRECISION, t1.SCALE, t2.LEVEL_NO
SELECT XMLELEMENT(""PLSQLTypes"",
       XMLAGG(
           XMLELEMENT(""Type"",
               XMLATTRIBUTES(
                   t1.TYPE_OWNER AS ""TypeOwner"",
                   t1.TYPE_NAME AS ""TypeName"",
                   t1.ATTR_TYPE AS ""AttrType""
               ),
               XMLAGG(
                   XMLELEMENT(""Member"",
                       XMLFOREST(
                           t1.ATTR_NAME AS ""AttrName"",
                           t1.ATTR_NO AS ""AttrNo"",
                           t1.ATTR_TYPE_OWNER AS ""AttrTypeOwner"",
                           t1.ATTR_TYPE_NAME AS ""DataType"",
                           t1.LENGTH AS ""DataLength"",
                           t1.PRECISION AS ""DataPrecision"",
                           t1.SCALE AS ""DataScale"",
                           t2.LEVEL_NO AS ""LevelNo""
                       )
                   ) ORDER BY t1.ATTR_NO
               )
           ) ORDER BY LEVEL_NO, t1.TYPE_OWNER, t1.TYPE_NAME
       )
) AS xml_output
FROM
   (SELECT UPPER(OWNER) TYPE_OWNER, UPPER(TYPE_NAME) TYPE_NAME, UPPER(COLL_TYPE) ATTR_TYPE, NULL ATTR_NAME, 1 ATTR_NO, UPPER(ELEM_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ELEM_TYPE_NAME) ATTR_TYPE_NAME, UPPER_BOUND LENGTH, PRECISION, SCALE
    FROM ALL_COLL_TYPES WHERE COLL_TYPE = 'VARYING ARRAY'
    UNION ALL
    SELECT UPPER(OWNER) TYPE_OWNER, UPPER(TYPE_NAME) TYPE_NAME, UPPER(COLL_TYPE) ATTR_TYPE, NULL ATTR_NAME, 1 ATTR_NO, UPPER(ELEM_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ELEM_TYPE_NAME) ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
    FROM ALL_COLL_TYPES WHERE COLL_TYPE = 'TABLE'
    UNION ALL
    SELECT UPPER(OWNER) TYPE_OWNER, UPPER(TYPE_NAME) TYPE_NAME, 'UDT' ATTR_TYPE, ATTR_NAME, ATTR_NO, UPPER(ATTR_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ATTR_TYPE_NAME) ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
    FROM ALL_TYPE_ATTRS
    ORDER BY TYPE_NAME, ATTR_NO) t1,
  (WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
  )
  --SELECT * FROM user_schemas
  , all_user_types AS (
      SELECT UNIQUE UPPER(OWNER) TYPE_OWNER, UPPER(TYPE_NAME) TYPE_NAME FROM ALL_COLL_TYPES WHERE OWNER IN (SELECT OWNER FROM user_schemas)
      UNION ALL
      SELECT UNIQUE UPPER(OWNER) TYPE_OWNER, UPPER(TYPE_NAME) TYPE_NAME FROM ALL_TYPE_ATTRS WHERE OWNER IN (SELECT OWNER FROM user_schemas)
  )
  --SELECT * FROM all_user_types ORDER BY TYPE_OWNER, TYPE_NAME
  , dependent_types AS (
      SELECT TYPE_OWNER, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME
      FROM (
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 UPPER(ATTR_TYPE_NAME) DEPENDENT_TYPE_NAME, 
                 UPPER(ATTR_TYPE_OWNER) DEPENDENT_TYPE_OWNER 
          FROM ALL_TYPE_ATTRS
          WHERE (OWNER, ATTR_TYPE_NAME) IN (SELECT TYPE_OWNER, TYPE_NAME FROM all_user_types)
          UNION  
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 UPPER(ELEM_TYPE_NAME) DEPENDENT_TYPE_NAME, 
                 UPPER(ELEM_TYPE_OWNER) DEPENDENT_TYPE_OWNER 
          FROM ALL_COLL_TYPES
          WHERE (ELEM_TYPE_OWNER, ELEM_TYPE_NAME) IN (SELECT TYPE_OWNER, TYPE_NAME FROM all_user_types)
          UNION  
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 NULL DEPENDENT_TYPE_NAME, 
                 NULL DEPENDENT_TYPE_OWNER 
          FROM ALL_COLL_TYPES
          WHERE (UPPER(ELEM_TYPE_OWNER), UPPER(ELEM_TYPE_NAME)) NOT IN (SELECT TYPE_OWNER, TYPE_NAME FROM all_user_types)
          AND OWNER IN (SELECT OWNER FROM user_schemas)
          UNION
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 NULL DEPENDENT_TYPE_NAME, 
                 NULL DEPENDENT_TYPE_OWNER
          FROM ALL_TYPE_ATTRS
          WHERE (UPPER(OWNER), UPPER(ATTR_TYPE_NAME)) NOT IN (SELECT TYPE_OWNER, TYPE_NAME FROM all_user_types)
          AND OWNER IN (SELECT OWNER FROM user_schemas)
      )
      --WHERE UPPER(TYPE_OWNER) IN (SELECT UPPER(OWNER) FROM user_schemas)
  )
  --SELECT * FROM dependent_types WHERE TYPE_OWNER = 'MERCH' ORDER BY TYPE_OWNER, TYPE_NAME
  , dependencies AS (
      SELECT TYPE_OWNER, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME FROM dependent_types
      UNION ALL
      SELECT DEPENDENT_TYPE_OWNER TYPE_OWNER, DEPENDENT_TYPE_NAME TYPE_NAME, NULL DEPENDENT_TYPE_OWNER, NULL DEPENDENT_TYPE_NAME
      FROM dependent_types
      WHERE DEPENDENT_TYPE_OWNER IS NOT NULL
      AND DEPENDENT_TYPE_NAME IS NOT NULL
      AND (DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME) NOT IN (SELECT TYPE_OWNER, TYPE_NAME FROM dependent_types) 
  )
  --SELECT * FROM dependencies WHERE TYPE_OWNER = 'MERCH' ORDER BY TYPE_OWNER, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME
  , recursive_sort (type_owner, type_name, dependent_type_owner, dependent_type_name, level_no) AS (
      -- Base case: Elements with no dependencies
      SELECT type_owner, type_name, dependent_type_owner, dependent_type_name, 1 AS level_no
      FROM dependencies
      WHERE dependent_type_name IS NULL

      UNION ALL

      -- Recursive case: Elements depending on already processed elements
      SELECT cq.type_owner, cq.type_name, cq.dependent_type_owner, cq.dependent_type_name, rs.level_no + 1
      FROM dependencies cq
      INNER JOIN recursive_sort rs ON cq.dependent_type_owner = rs.type_owner AND cq.dependent_type_name = rs.type_name
  )
  --SELECT * FROM recursive_sort WHERE TYPE_OWNER = 'MERCH' ORDER BY LEVEL_NO, TYPE_NAME, DEPENDENT_TYPE_NAME
  SELECT type_owner, type_name, dependent_type_owner, dependent_type_name, level_no
  FROM (
    SELECT type_owner, type_name, dependent_type_owner, dependent_type_name, level_no
    FROM (
        SELECT type_owner, type_name, dependent_type_owner, dependent_type_name, level_no,
               ROW_NUMBER() OVER (PARTITION BY type_owner, type_name ORDER BY level_no DESC) AS rn
        FROM recursive_sort
    )
    WHERE rn = 1
    ORDER BY LEVEL_NO
  ) --WHERE TYPE_OWNER = 'MERCH'
) t2
WHERE T1.TYPE_OWNER = t2.TYPE_OWNER
AND T1.TYPE_NAME = t2.TYPE_NAME
GROUP BY LEVEL_NO, t1.TYPE_OWNER, t1.TYPE_NAME, t1.ATTR_TYPE
--ORDER BY LEVEL_NO, t1.TYPE_OWNER, t1.TYPE_NAME, ATTR_NO
";

        private static readonly string tablesQuery = @"
-- Tables, views and materialized views.
--SELECT *
SELECT XMLELEMENT(""Tables"",
       XMLAGG(
           XMLELEMENT(""Table"",
               XMLATTRIBUTES(t.OWNER AS ""Schema"", t.TABLE_NAME AS ""TableName""),
               XMLAGG(
                   XMLELEMENT(""Column"",
                       XMLFOREST(
                           t.COLUMN_NAME AS ""ColumnName"",
                           t.DATA_TYPE AS ""DataType"",
                           t.DATA_LENGTH AS ""DataLength"",
                           t.DATA_PRECISION AS ""DataPrecision"",
                           t.DATA_SCALE AS ""DataScale"",
                           t.NULLABLE AS ""Nullable"",
                           t.IDENTITY_COLUMN AS ""IdentityColumn"",
                           t.COLUMN_ID AS ""ColumnId""
                       )
                   ) ORDER BY t.COLUMN_ID
               )
           )
       )
) AS xml_output
FROM (
    WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
    )
    SELECT OWNER, TABLE_NAME, COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, IDENTITY_COLUMN, COLUMN_ID
    FROM ALL_TAB_COLUMNS
    WHERE OWNER IN (SELECT OWNER FROM user_schemas)
) t
GROUP BY t.OWNER, t.TABLE_NAME
--ORDER BY t.OWNER, t.TABLE_NAME
";

        private static readonly string recordsQuery = @"
--SELECT t1.TYPE_OWNER, t1.PACKAGE_NAME, t1.TYPE_NAME, t1.ATTR_TYPE, t1.ATTR_NAME, t1.ATTR_NO, t1.ATTR_TYPE_OWNER, t1.ATTR_TYPE_NAME, t1.LENGTH, t1.PRECISION, t1.SCALE, t2.LEVEL_NO
SELECT XMLELEMENT(""PLSQLTypes"",
       XMLAGG(
           XMLELEMENT(""Type"",
               XMLATTRIBUTES(
                   t1.TYPE_OWNER AS ""TypeOwner"",
                   t1.PACKAGE_NAME AS ""PackageName"",
                   t1.TYPE_NAME AS ""TypeName"",
                   t1.ATTR_TYPE AS ""AttrType""
               ),
               XMLAGG(
                   XMLELEMENT(""Member"",
                       XMLFOREST(
                           t1.ATTR_NAME AS ""AttrName"",
                           t1.ATTR_NO AS ""AttrNo"",
                           t1.ATTR_TYPE_OWNER AS ""AttrTypeOwner"",
                           t1.ATTR_TYPE_NAME AS ""DataType"",
                           t1.LENGTH AS ""DataLength"",
                           t1.PRECISION AS ""DataPrecision"",
                           t1.SCALE AS ""DataScale"",
                           t2.LEVEL_NO AS ""LevelNo""
                       )
                   ) ORDER BY t1.ATTR_NO
               )
           ) ORDER BY t1.TYPE_OWNER, t1.PACKAGE_NAME, LEVEL_NO
       )
) AS xml_output
FROM
   (SELECT UPPER(OWNER) TYPE_OWNER, UPPER(PACKAGE_NAME) PACKAGE_NAME, UPPER(TYPE_NAME) TYPE_NAME, UPPER(COLL_TYPE) ATTR_TYPE, NULL ATTR_NAME, 1 ATTR_NO, UPPER(ELEM_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ELEM_TYPE_NAME) ATTR_TYPE_NAME, UPPER_BOUND LENGTH, PRECISION, SCALE
    FROM ALL_PLSQL_COLL_TYPES WHERE COLL_TYPE = 'VARYING ARRAY'
    UNION ALL
    SELECT UPPER(OWNER) TYPE_OWNER, UPPER(PACKAGE_NAME) PACKAGE_NAME, UPPER(TYPE_NAME) TYPE_NAME, 'TABLE' ATTR_TYPE, NULL ATTR_NAME, 1 ATTR_NO, UPPER(ELEM_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ELEM_TYPE_NAME) ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
    FROM ALL_PLSQL_COLL_TYPES WHERE COLL_TYPE IN ('TABLE', 'PL/SQL INDEX TABLE')
    UNION ALL
    SELECT UPPER(OWNER) TYPE_OWNER, UPPER(PACKAGE_NAME) PACKAGE_NAME, UPPER(TYPE_NAME) TYPE_NAME, 'UDT' ATTR_TYPE, ATTR_NAME, ATTR_NO, UPPER(ATTR_TYPE_OWNER) ATTR_TYPE_OWNER, UPPER(ATTR_TYPE_NAME) ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
    FROM ALL_PLSQL_TYPE_ATTRS WHERE TYPE_NAME NOT LIKE '%\%%' ESCAPE '\'
    ORDER BY PACKAGE_NAME, TYPE_NAME, ATTR_NO) t1,
  (WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
  )
  --SELECT * FROM user_schemas
  , all_user_types AS (
      SELECT UNIQUE UPPER(OWNER) TYPE_OWNER, UPPER(PACKAGE_NAME) PACKAGE_NAME, UPPER(TYPE_NAME) TYPE_NAME FROM ALL_PLSQL_COLL_TYPES WHERE OWNER IN (SELECT OWNER FROM user_schemas)
      UNION ALL
      SELECT UNIQUE UPPER(OWNER) TYPE_OWNER, UPPER(PACKAGE_NAME) PACKAGE_NAME, UPPER(TYPE_NAME) TYPE_NAME FROM ALL_PLSQL_TYPE_ATTRS WHERE OWNER IN (SELECT OWNER FROM user_schemas)
  )
  --SELECT * FROM all_user_types ORDER BY TYPE_OWNER, PACKAGE_NAME, TYPE_NAME
  , dependent_types AS (
      SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME
      FROM (
          SELECT UPPER(OWNER) TYPE_OWNER,
                 UPPER(PACKAGE_NAME) PACKAGE_NAME,
                 UPPER(TYPE_NAME) TYPE_NAME,
                 UPPER(ATTR_TYPE_NAME) DEPENDENT_TYPE_NAME,
                 UPPER(ATTR_TYPE_OWNER) DEPENDENT_TYPE_OWNER
          FROM ALL_PLSQL_TYPE_ATTRS
          WHERE (OWNER, PACKAGE_NAME, ATTR_TYPE_NAME) IN (SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME FROM all_user_types)
          UNION  
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(PACKAGE_NAME) PACKAGE_NAME, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 UPPER(ELEM_TYPE_NAME) DEPENDENT_TYPE_NAME, 
                 UPPER(ELEM_TYPE_OWNER) DEPENDENT_TYPE_OWNER 
          FROM ALL_PLSQL_COLL_TYPES
          WHERE (ELEM_TYPE_OWNER, ELEM_TYPE_PACKAGE, ELEM_TYPE_NAME) IN (SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME FROM all_user_types)
          UNION  
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(PACKAGE_NAME) PACKAGE_NAME, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 NULL DEPENDENT_TYPE_NAME, 
                 NULL DEPENDENT_TYPE_OWNER
          FROM ALL_PLSQL_COLL_TYPES
          WHERE (UPPER(ELEM_TYPE_OWNER), UPPER(PACKAGE_NAME), UPPER(ELEM_TYPE_NAME)) NOT IN (SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME FROM all_user_types)
          AND OWNER IN (SELECT OWNER FROM user_schemas)
          UNION  
          SELECT UPPER(OWNER) TYPE_OWNER, 
                 UPPER(PACKAGE_NAME) PACKAGE_NAME, 
                 UPPER(TYPE_NAME) TYPE_NAME, 
                 NULL DEPENDENT_TYPE_NAME, 
                 NULL DEPENDENT_TYPE_OWNER 
          FROM ALL_PLSQL_TYPE_ATTRS
          WHERE (UPPER(OWNER), UPPER(PACKAGE_NAME), UPPER(ATTR_TYPE_NAME)) NOT IN (SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME FROM all_user_types)
          AND OWNER IN (SELECT OWNER FROM user_schemas)
      )
      --WHERE UPPER(TYPE_OWNER) IN (SELECT UPPER(OWNER) FROM user_schemas)
  )
  --SELECT * FROM dependent_types WHERE TYPE_OWNER = 'MERCH' ORDER BY TYPE_OWNER, PACKAGE_NAME, TYPE_NAME
  , dependencies AS (
      SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME FROM dependent_types
      UNION ALL
      SELECT DEPENDENT_TYPE_OWNER TYPE_OWNER, PACKAGE_NAME, DEPENDENT_TYPE_NAME TYPE_NAME, NULL DEPENDENT_TYPE_OWNER, NULL DEPENDENT_TYPE_NAME
      FROM dependent_types
      WHERE DEPENDENT_TYPE_OWNER IS NOT NULL
      AND DEPENDENT_TYPE_NAME IS NOT NULL
      AND (DEPENDENT_TYPE_OWNER, PACKAGE_NAME, DEPENDENT_TYPE_NAME) NOT IN (SELECT TYPE_OWNER, PACKAGE_NAME, TYPE_NAME FROM dependent_types) 
  )
  --SELECT * FROM dependencies WHERE TYPE_OWNER = 'MERCH' ORDER BY TYPE_OWNER, TYPE_NAME, DEPENDENT_TYPE_OWNER, DEPENDENT_TYPE_NAME
  , recursive_sort (type_owner, package_name, type_name, dependent_type_owner, dependent_type_name, level_no) AS (
      -- Base case: Elements with no dependencies
      SELECT type_owner, package_name, type_name, dependent_type_name, dependent_type_owner, 1 AS level_no
      FROM dependencies
      WHERE dependent_type_name IS NULL

      UNION ALL

      -- Recursive case: Elements depending on already processed elements
      SELECT cq.type_owner, cq.package_name, cq.type_name, cq.dependent_type_name, cq.dependent_type_owner, rs.level_no + 1
      FROM dependencies cq
      INNER JOIN recursive_sort rs ON cq.dependent_type_owner = rs.type_owner AND cq.dependent_type_owner = rs.type_owner AND cq.dependent_type_name = rs.type_name
  )
  --SELECT * FROM recursive_sort WHERE TYPE_OWNER = 'MERCH' ORDER BY LEVEL_NO, TYPE_NAME, DEPENDENT_TYPE_NAME
  SELECT type_owner, package_name, type_name, dependent_type_owner, dependent_type_name, level_no
  FROM (
    SELECT type_owner, package_name, type_name, dependent_type_owner, dependent_type_name, level_no
    FROM (
        SELECT type_owner, package_name, type_name, dependent_type_owner, dependent_type_name, level_no,
               ROW_NUMBER() OVER (PARTITION BY type_owner, package_name, type_name ORDER BY level_no DESC) AS rn
        FROM recursive_sort
    )
    WHERE rn = 1
    ORDER BY LEVEL_NO
  ) --WHERE TYPE_OWNER = 'MERCH'
) t2
WHERE T1.TYPE_OWNER = t2.TYPE_OWNER
AND T1.PACKAGE_NAME = t2.PACKAGE_NAME
AND T1.TYPE_NAME = t2.TYPE_NAME
GROUP BY t1.TYPE_OWNER, t1.PACKAGE_NAME, t2.LEVEL_NO, t1.TYPE_NAME, t1.ATTR_TYPE
--ORDER BY t1.TYPE_OWNER, t1.PACKAGE_NAME, LEVEL_NO, t1.TYPE_NAME, t1.ATTR_NO
";

        private static readonly string proceduresQuery = @"
--SELECT *
SELECT XMLELEMENT(""Objects"",
       XMLAGG(
           XMLELEMENT(""Object"",
               XMLATTRIBUTES(t.OWNER AS ""Schema"", t.PACKAGE_NAME AS ""PackageName"", t.OBJECT_NAME AS ""ObjectName""),
               XMLAGG(
                   XMLELEMENT(""Argument"",
                       XMLFOREST(
                           t.ARGUMENT_NAME AS ""ArgumentName"",
                           t.DATA_TYPE AS ""DataType"",
                           t.IN_OUT AS ""InOut"",
                           t.DATA_LENGTH AS ""DataLength"",
                           t.DATA_PRECISION AS ""DataPrecision"",
                           t.DATA_SCALE AS ""DataScale"",
                           t.TYPE_OWNER AS ""TypeOwner"",
                           t.TYPE_NAME AS ""TypeName"",
                           t.TYPE_SUBNAME AS ""TypeSubname"",
                           t.TYPE_OBJECT_TYPE AS ""TypeObjectType"",
                           t.PLS_TYPE AS ""PlsType""
                       )
                   ) ORDER BY t.POSITION
               )
           ) ORDER BY t.OWNER, t.PACKAGE_NAME -- Ordering by schema first, then package name
       )
) AS xml_output
FROM (
    -- Your existing query logic here
    WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
    )
    SELECT OWNER, PACKAGE_NAME, OBJECT_NAME, OVERLOAD, ARGUMENT_NAME, DATA_TYPE, IN_OUT, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, POSITION, TYPE_OWNER, TYPE_NAME, TYPE_SUBNAME, TYPE_OBJECT_TYPE, PLS_TYPE
    FROM ALL_ARGUMENTS
    WHERE OWNER IN (SELECT OWNER FROM user_schemas)
) t
GROUP BY t.OWNER, t.PACKAGE_NAME, t.OBJECT_NAME, t.OVERLOAD
--ORDER BY t.OWNER, t.PACKAGE_NAME, t.OBJECT_NAME, t.OVERLOAD
";

        private static readonly string sequencesQuery = @"
--SELECT *
SELECT XMLELEMENT(""Sequences"",
       XMLAGG(
           XMLELEMENT(""Sequence"",
               XMLATTRIBUTES(t.SEQUENCE_OWNER AS ""Schema"", t.SEQUENCE_NAME AS ""SequenceName""),
               XMLFOREST(
                   t.MIN_VALUE AS ""MinValue"",
                   t.MAX_VALUE AS ""MaxValue"",
                   t.INCREMENT_BY AS ""IncrementBy"",
                   t.CYCLE_FLAG AS ""CycleFlag"",
                   t.ORDER_FLAG AS ""OrderFlag"",
                   t.CACHE_SIZE AS ""CacheSize"",
                   t.LAST_NUMBER AS ""LastNumber""
               )
           ) ORDER BY t.SEQUENCE_OWNER, t.SEQUENCE_NAME
       )
) AS xml_output
FROM (
    WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
    )
    SELECT SEQUENCE_OWNER, SEQUENCE_NAME, MIN_VALUE, MAX_VALUE, INCREMENT_BY, CYCLE_FLAG, ORDER_FLAG, CACHE_SIZE, LAST_NUMBER
    FROM ALL_SEQUENCES
    WHERE SEQUENCE_OWNER IN (SELECT OWNER FROM user_schemas)
    AND SEQUENCE_NAME NOT LIKE 'ISEQ$$%'
) t
--ORDER BY t.SEQUENCE_OWNER, t.SEQUENCE_NAME
";

        private static readonly string synonymsQuery = @"
--SELECT *
SELECT XMLELEMENT(""Synonyms"",
       XMLAGG(
           XMLELEMENT(""Synonym"",
               XMLATTRIBUTES(t.OWNER AS ""Schema"", t.SYNONYM_NAME AS ""SynonymName"", t.TABLE_OWNER AS ""ObjectOwner"", t.TABLE_NAME AS ""ObjectName"", t.DB_LINK AS ""DbLink"")
           ) ORDER BY t.OWNER, t.SYNONYM_NAME
       )
) AS xml_output
FROM (
    WITH user_schemas AS (
        SELECT UNIQUE OWNER 
        FROM ALL_OBJECTS
        WHERE ORACLE_MAINTAINED = 'N'
        AND OWNER IN (SELECT USERNAME AS SCHEMA_NAME FROM DBA_USERS WHERE ORACLE_MAINTAINED = 'N')
        AND OBJECT_TYPE IN ('FUNCTION', 'MATERIALIZED VIEW', 'PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'SEQUENCE', 'TABLE', 'TYPE', 'VIEW')
    )
    SELECT *
    FROM ALL_SYNONYMS
    WHERE OWNER IN (SELECT OWNER FROM user_schemas)
) t
--ORDER BY t.SEQUENCE_OWNER, t.SEQUENCE_NAME
";

        [GeneratedRegex("^((?<user>[^/]+)/(?<password>[^@]+)@(?<host>[^:/]+)(:(?<port>[0-9]+))?/(?<server>[^/]+))$")]
        private static partial Regex DbConnectionRegex();

        private static readonly Regex _databaseValues = DbConnectionRegex();

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: ExtractOracleDbMetadata username/password@hostname[:port]/database output-folder");
                return;
            }

            Match match = _databaseValues.Match(args[0]);

            if (match.Success == false)
            {
                Console.WriteLine("Invalid database connection parameters");
                return;
            }

            string user = match.Groups["user"].Value;
            string password = match.Groups["password"].Value;
            string host = match.Groups["host"].Value;
            string port = match.Groups["port"].Value;
            port = string.IsNullOrEmpty(port) ? "1521" : port;
            string server = match.Groups["server"].Value;

            string connectionString = $"DATA SOURCE=(DESCRIPTION=(ADDRESS=(PROTOCOL=tcp)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={server})));PASSWORD={password};USER ID={user}";

            try
            {
                // Check if the output folder exists.
                if (!Directory.Exists(args[1]))
                    Directory.CreateDirectory(args[1]);

                string tempPath = Path.GetTempPath();
                string uniqueFolder = Path.Combine(tempPath, Path.GetRandomFileName());

                Directory.CreateDirectory(uniqueFolder);

                ExtractMetadata("schemas", schemasQuery, connectionString, uniqueFolder);
                ExtractMetadata("types", typesQuery, connectionString, uniqueFolder);
                ExtractMetadata("tables", tablesQuery, connectionString, uniqueFolder);
                ExtractMetadata("records", recordsQuery, connectionString, uniqueFolder);
                ExtractMetadata("procedures", proceduresQuery, connectionString, uniqueFolder);
                ExtractMetadata("sequences", sequencesQuery, connectionString, uniqueFolder);
                ExtractMetadata("synonyms", synonymsQuery, connectionString, uniqueFolder);

                ZipSelectedFiles(uniqueFolder, $@"{args[1]}\DbMetadata_{DateTime.Now:yyyy-MM-dd_HH.mm.ss}.zip");

                // Clean up and remove the folder and its contents.
                Directory.Delete(uniqueFolder, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private static void ExtractMetadata(string name, string sqlQuery, string connectionString, string outputFilePath)
        {
            Console.WriteLine($"Extracting {name} metadata...");

            using OracleConnection connection = new(connectionString);
            connection.Open();

            using OracleCommand command = new(sqlQuery, connection);
            using OracleDataReader reader = command.ExecuteReader();

            reader.Read();
            string value = "<?xml version=\"1.0\"?>" + reader[0]?.ToString() ?? "";

            File.WriteAllText(Path.Combine(outputFilePath, $"{name}.xml"), value);
        }

        static void ZipSelectedFiles(string folderPath, string zipPath)
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath); // Ensure the zip file does not already exist

            using FileStream zipStream = new(zipPath, FileMode.Create);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);
            foreach (string file in Directory.GetFiles(folderPath, "*.xml"))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

    }
}
