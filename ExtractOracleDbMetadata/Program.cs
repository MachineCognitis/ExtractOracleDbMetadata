using Oracle.ManagedDataAccess.Client;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text;

namespace ExtractOracleDbMetadata
{
    internal partial class Program
    {
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
                string query;
                string outputFilePath;
                PlSqlUnwrapper.Encoding = GetDbEncoding(connectionString);

                // Check if the output folder exists
                if (!Directory.Exists(args[1]))
                    Directory.CreateDirectory(args[1]);

                string tempPath = Path.GetTempPath();
                string uniqueFolder = Path.Combine(tempPath, Path.GetRandomFileName());

                Directory.CreateDirectory(uniqueFolder);

                Console.WriteLine("Extracting tables metadata...");

                query = @"SELECT TABLE_NAME, COLUMN_NAME,DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, IDENTITY_COLUMN
                                 FROM USER_TAB_COLUMNS
                                 ORDER BY TABLE_NAME, COLUMN_ID";
                outputFilePath = $@"{uniqueFolder}\Tables.csv";
                ExtractData(connectionString, query, outputFilePath);
                //OpenFile(outputFilePath);

                query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('TABLE', table_name, owner), '<END>' DDL
                           FROM ALL_TABLES
                           WHERE OWNER = '{user}'";
                outputFilePath = $@"{uniqueFolder}\Tables_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false);
                //OpenFile(outputFilePath);

                Console.WriteLine("Extracting sequences metadata...");

                query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('SEQUENCE', sequence_name, sequence_owner), '<END>' DDL
                           FROM ALL_SEQUENCES
                           WHERE SEQUENCE_OWNER = '{user}'
                           AND SEQUENCE_NAME NOT LIKE 'ISEQ$$%'";
                outputFilePath = $@"{uniqueFolder}\Sequences_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments);
                //OpenFile(outputFilePath);

                //Console.WriteLine("Extracting materialized views metadata...");

                //query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('MATERIALIZED_VIEW', mview_name, owner), '<END>' DDL
                //           FROM ALL_MVIEWS
                //           WHERE OWNER = '{user}'";
                //outputFilePath = $@"{uniqueFolder}\MaterializedViews_DDL.txt";
                //ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments);
                ////OpenFile(outputFilePath);

                //Console.WriteLine("Extracting views metadata...");

                //query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('VIEW', view_name, owner), '<END>' DDL
                //           FROM ALL_VIEWS
                //           WHERE OWNER = '{user}'";
                //outputFilePath = $@"{uniqueFolder}\Views_DDL.txt";
                //ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, filter: RemovePlSqlComments);
                ////OpenFile(outputFilePath);

                Console.WriteLine("Extracting procedures metadata...");

                query = @"SELECT PACKAGE_NAME, OBJECT_NAME, ARGUMENT_NAME, DATA_TYPE, IN_OUT, DATA_LENGTH, DATA_PRECISION, DATA_SCALE
                          FROM USER_ARGUMENTS
                          ORDER BY PACKAGE_NAME, OBJECT_NAME, POSITION";
                outputFilePath = $@"{uniqueFolder}\Procedures.csv";
                ExtractData(connectionString, query, outputFilePath);
                //OpenFile(outputFilePath);

                query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('PROCEDURE', object_name, owner), '<END>' DDL
                           FROM ALL_OBJECTS
                           WHERE OWNER = '{user}' AND OBJECT_TYPE = 'PROCEDURE'";
                outputFilePath = $@"{uniqueFolder}\Procedures_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments, UnwrapCode, RemovePlSqlComments, RemoveProcedureBody, CollapseBlankLines);
                //OpenFile(outputFilePath);

                Console.WriteLine("Extracting functions metadata...");

                query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('FUNCTION', object_name, owner), '<END>' DDL
                           FROM ALL_OBJECTS
                           WHERE OWNER = '{user}' AND OBJECT_TYPE = 'FUNCTION'";
                outputFilePath = $@"{uniqueFolder}\Functions_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments, UnwrapCode, RemovePlSqlComments, RemoveFunctionBody, CollapseBlankLines);
                //OpenFile(outputFilePath);

                Console.WriteLine("Extracting packages metadata...");

                query = $@"SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('PACKAGE', object_name, owner), '<END>' DDL
                           FROM ALL_OBJECTS
                           WHERE OWNER = '{user}' AND OBJECT_TYPE = 'PACKAGE'";
                outputFilePath = $@"{uniqueFolder}\Packages_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments, RemovePackageBody, RemovePlSqlComments, CollapseBlankLines);
                //OpenFile(outputFilePath);

                Console.WriteLine("Extracting types metadata...");

                query = @"SELECT t1.TYPE_NAME, t1.ATTR_TYPE, t1.ATTR_NAME, t1.ATTR_NO, t1.ATTR_TYPE_NAME, t1.LENGTH, t1.PRECISION, t1.SCALE, t2.LEVEL_NO
                          FROM
                             (SELECT TYPE_NAME, COLL_TYPE ATTR_TYPE, NULL ATTR_NAME, 1 ATTR_NO, ELEM_TYPE_NAME ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
                              FROM USER_COLL_TYPES
                              UNION ALL
                              SELECT TYPE_NAME, 'UDT' ATTR_TYPE, ATTR_NAME, ATTR_NO, ATTR_TYPE_NAME, LENGTH, PRECISION, SCALE
                              FROM USER_TYPE_ATTRS
                              ORDER BY TYPE_NAME, ATTR_NO) t1, 
                             (WITH coll_types AS (
                                  SELECT UNIQUE TYPE_NAME FROM USER_COLL_TYPES UNION ALL SELECT UNIQUE TYPE_NAME FROM USER_TYPE_ATTRS
                              ),
                              dependent_types AS (
                                  SELECT TYPE_NAME element_name, dependency_name
                                  FROM (
                                        SELECT TYPE_NAME, ATTR_TYPE_NAME DEPENDENCY_NAME FROM USER_TYPE_ATTRS
                                        WHERE ATTR_TYPE_NAME IN (SELECT * FROM coll_types)
                                        UNION ALL  
                                        SELECT TYPE_NAME, ELEM_TYPE_NAME DEPENDENCY_NAME FROM USER_COLL_TYPES
                                        WHERE ELEM_TYPE_NAME IN (SELECT * FROM coll_types)
                                        UNION ALL  
                                        SELECT TYPE_NAME, NULL DEPENDENCY_NAME FROM USER_COLL_TYPES
                                        WHERE ELEM_TYPE_NAME NOT IN (SELECT * FROM coll_types)
                                  )
                              )
                              --SELECT * FROM dependent_types
                              ,
                              dependencies AS (
                                  SELECT * FROM dependent_types
                                  UNION ALL
                                  SELECT DEPENDENCY_NAME element_name, NULL
                                  FROM dependent_types
                                  WHERE DEPENDENCY_NAME NOT IN (SELECT element_name FROM dependent_types) 
                              )
                              --SELECT * FROM dependent_types
                              ,
                              recursive_sort (element_name, dependency_name, level_no) AS (
                                  -- Base case: Elements with no dependencies
                                  SELECT element_name, dependency_name, 1 AS level_no
                                  FROM dependencies
                                  WHERE dependency_name IS NULL

                                  UNION ALL

                                  -- Recursive case: Elements depending on already processed elements
                                  SELECT cq.element_name, cq.dependency_name, rs.level_no + 1
                                  FROM dependencies cq
                                  INNER JOIN recursive_sort rs ON cq.dependency_name = rs.element_name
                              )
                              --SELECT DBMS_METADATA.GET_DDL('TYPE', element_name, 'MERCH')
                              SELECT element_name, dependency_name, level_no
                              FROM (
                                SELECT element_name, dependency_name, level_no
                                FROM (
                                    SELECT element_name, dependency_name, level_no,
                                           ROW_NUMBER() OVER (PARTITION BY element_name ORDER BY level_no DESC) AS rn
                                    FROM recursive_sort
                                --    WHERE element_name = dependency_name
                                ) subquery
                                WHERE rn = 1
                                ORDER BY LEVEL_NO
                              )) t2
                          WHERE T1.TYPE_NAME = t2.element_name
                          ORDER BY LEVEL_NO, TYPE_NAME, ATTR_NO";
                outputFilePath = $@"{uniqueFolder}\Types.csv";
                ExtractData(connectionString, query, outputFilePath);
                //OpenFile(outputFilePath);

                query = $@"WITH coll_types AS (
                               SELECT UNIQUE TYPE_NAME FROM USER_COLL_TYPES UNION ALL SELECT UNIQUE TYPE_NAME FROM USER_TYPE_ATTRS
                           ),
                           dependent_types AS (
                               SELECT TYPE_NAME element_name, dependency_name
                               FROM (
                                     SELECT TYPE_NAME, ATTR_TYPE_NAME DEPENDENCY_NAME FROM USER_TYPE_ATTRS
                                     WHERE ATTR_TYPE_NAME IN (SELECT * FROM coll_types)
                                     UNION ALL  
                                     SELECT TYPE_NAME, ELEM_TYPE_NAME DEPENDENCY_NAME FROM USER_COLL_TYPES
                                     WHERE ELEM_TYPE_NAME IN (SELECT * FROM coll_types)
                                     UNION ALL  
                                     SELECT TYPE_NAME, NULL DEPENDENCY_NAME FROM USER_COLL_TYPES
                                     WHERE ELEM_TYPE_NAME NOT IN (SELECT * FROM coll_types)
                               )
                           )
                           --SELECT * FROM dependent_types
                           ,
                           dependencies AS (
                               SELECT * FROM dependent_types
                               UNION ALL
                               SELECT DEPENDENCY_NAME element_name, NULL
                               FROM dependent_types
                               WHERE DEPENDENCY_NAME NOT IN (SELECT element_name FROM dependent_types) 
                           )
                           --SELECT * FROM dependent_types
                           ,
                           recursive_sort (element_name, dependency_name, level_no) AS (
                               -- Base case: Elements with no dependencies
                               SELECT element_name, dependency_name, 1 AS level_no
                               FROM dependencies
                               WHERE dependency_name IS NULL

                               UNION ALL

                               -- Recursive case: Elements depending on already processed elements
                               SELECT cq.element_name, cq.dependency_name, rs.level_no + 1
                               FROM dependencies cq
                               INNER JOIN recursive_sort rs ON cq.dependency_name = rs.element_name
                           )
                           SELECT '<BEGIN>', DBMS_METADATA.GET_DDL('TYPE', element_name, 'MERCH'), '<END>' DDL
                           --SELECT element_name, dependency_name, level_no
                           FROM (
                             SELECT element_name, dependency_name, level_no
                             FROM (
                                 SELECT element_name, dependency_name, level_no,
                                        ROW_NUMBER() OVER (PARTITION BY element_name ORDER BY level_no DESC) AS rn
                                 FROM recursive_sort
                             --    WHERE element_name = dependency_name
                             ) subquery
                             WHERE rn = 1
                             ORDER BY LEVEL_NO
                           )";
                outputFilePath = $@"{uniqueFolder}\Types_DDL.txt";
                ExtractData(connectionString, query, outputFilePath, writeColumnNames: false, RemovePlSqlComments);
                //OpenFile(outputFilePath);

                ZipSelectedFiles(uniqueFolder, $@"{args[1]}\DbMetadata_{DateTime.Now:yyyy-MM-dd_HH.mm.ss}.zip");

                // Clean up and remove the folder and its contents
                Directory.Delete(uniqueFolder, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private static void ExtractData(string connectionString, string query, string outputFilePath, bool writeColumnNames = true, params Func<string, string>[] filters)
        {
            using StreamWriter writer = new(outputFilePath);
            using OracleConnection connection = new(connectionString);
            connection.Open();

            using OracleCommand command = new(query, connection);
            using OracleDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (writeColumnNames)
                {
                    List<string> columnNames = [];
                    for (int i = 0; i < reader.FieldCount; i++)
                        columnNames.Add(reader.GetName(i));
                    writer.WriteLine(string.Join(',', columnNames));

                    writeColumnNames = false;
                }
                // Loop through all columns in the current row
                List<string> values = [];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string value = reader[i]?.ToString() ?? "";
                    foreach (Func<string, string> filter in filters)
                        value = filter(value);
                    values.Add(value);
                }
                writer.WriteLine(string.Join(',', values));
            }
        }

        private static Encoding GetDbEncoding(string connectionString)
        {
            using OracleConnection connection = new(connectionString);
            connection.Open();

            using OracleCommand command = new("SELECT VALUE FROM nls_database_parameters WHERE parameter = 'NLS_CHARACTERSET'", connection);
            string nlsCharacterSet = (string)command.ExecuteScalar();

            return nlsCharacterSet switch
            {
                "AL32UTF8" or "UTF8" => Encoding.UTF8,
                "WE8ISO8859P1" => Encoding.Latin1,
                "US7ASCII" => Encoding.ASCII,
                "CL8MSWIN1251" => Encoding.GetEncoding(1251),
                "CL8MSWIN1252" => Encoding.GetEncoding(1252),
                "JA16SJIS" => Encoding.GetEncoding("Shift_JIS"),
                "ZHS16GBK" => Encoding.GetEncoding("GBK"),
                _ => throw new InvalidDataException($"Unsupported NLS_CHARACTERSET: {nlsCharacterSet}"),
            };
        }

        static void ZipSelectedFiles(string folderPath, string zipPath)
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath); // Ensure the zip file does not already exist

            using FileStream zipStream = new(zipPath, FileMode.Create);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);
            foreach (string file in Directory.GetFiles(folderPath, "*.txt")
                .Concat(Directory.GetFiles(folderPath, "*.csv")))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        private static void OpenFile(string filePath)
        {
            ProcessStartInfo startInfo = new(filePath)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }

        static string RemovePlSqlComments(string input)
        {
            string pattern1 = @"--.*?$";
            string pattern2 = @"\/\*.*?\*\/";
            input = Regex.Replace(input, pattern1, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return Regex.Replace(input, pattern2, "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        static string UnwrapCode(string input)
        {
            if (input.Contains("wrapped"))
            {
                string[] wrappedCodes = input.Split("wrapped");
                StringBuilder result = new();

                for (int i = 1; i < wrappedCodes.Length; i++)
                {
                    string[] wrapped = wrappedCodes[i].Split(",<END>");
                    string unwrapped = PlSqlUnwrapper.Unwrap("wrapped" + UnwrapCode(wrapped[0]).TrimEnd());
                    result.Append(input.Replace("wrapped" + wrapped[0], unwrapped));
                }

                return result.ToString();
            }

            return input;
        }

        static string RemovePackageBody(string input)
        {
            string pattern = @"CREATE.+PACKAGE\sBODY";
            Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return input[..match.Index];

            return input;
        }

        static string RemoveProcedureBody(string input)
        {
            string pattern = @"(\)|\s)(AS|IS)\s+";
            Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (input[match.Index] == ')')
                    return input[..(match.Index + 1)];
                else
                    return input[..match.Index];
            }

            return input;
        }

        static string RemoveFunctionBody(string input)
        {
            string pattern = @"\s+(AS|IS)\s+";
            Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return input[..match.Index];

            return input;
        }

        static string CollapseBlankLines(string input)
        {
            string pattern = @"(\r?\n){2,}";
            return Regex.Replace(input, pattern, "\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        private static class PlSqlUnwrapper
        {
            public static Encoding Encoding { get; set; } = Encoding.UTF8;

            private static readonly byte[] substitutionTable =
            [
                0x3D, 0x65, 0x85, 0xB3, 0x18, 0xDB, 0xE2, 0x87, 0xF1, 0x52, 0xAB, 0x63, 0x4B, 0xB5, 0xA0, 0x5F,
                0x7D, 0x68, 0x7B, 0x9B, 0x24, 0xC2, 0x28, 0x67, 0x8A, 0xDE, 0xA4, 0x26, 0x1E, 0x03, 0xEB, 0x17,
                0x6F, 0x34, 0x3E, 0x7A, 0x3F, 0xD2, 0xA9, 0x6A, 0x0F, 0xE9, 0x35, 0x56, 0x1F, 0xB1, 0x4D, 0x10,
                0x78, 0xD9, 0x75, 0xF6, 0xBC, 0x41, 0x04, 0x81, 0x61, 0x06, 0xF9, 0xAD, 0xD6, 0xD5, 0x29, 0x7E,
                0x86, 0x9E, 0x79, 0xE5, 0x05, 0xBA, 0x84, 0xCC, 0x6E, 0x27, 0x8E, 0xB0, 0x5D, 0xA8, 0xF3, 0x9F,
                0xD0, 0xA2, 0x71, 0xB8, 0x58, 0xDD, 0x2C, 0x38, 0x99, 0x4C, 0x48, 0x07, 0x55, 0xE4, 0x53, 0x8C,
                0x46, 0xB6, 0x2D, 0xA5, 0xAF, 0x32, 0x22, 0x40, 0xDC, 0x50, 0xC3, 0xA1, 0x25, 0x8B, 0x9C, 0x16,
                0x60, 0x5C, 0xCF, 0xFD, 0x0C, 0x98, 0x1C, 0xD4, 0x37, 0x6D, 0x3C, 0x3A, 0x30, 0xE8, 0x6C, 0x31,
                0x47, 0xF5, 0x33, 0xDA, 0x43, 0xC8, 0xE3, 0x5E, 0x19, 0x94, 0xEC, 0xE6, 0xA3, 0x95, 0x14, 0xE0,
                0x9D, 0x64, 0xFA, 0x59, 0x15, 0xC5, 0x2F, 0xCA, 0xBB, 0x0B, 0xDF, 0xF2, 0x97, 0xBF, 0x0A, 0x76,
                0xB4, 0x49, 0x44, 0x5A, 0x1D, 0xF0, 0x00, 0x96, 0x21, 0x80, 0x7F, 0x1A, 0x82, 0x39, 0x4F, 0xC1,
                0xA7, 0xD7, 0x0D, 0xD1, 0xD8, 0xFF, 0x13, 0x93, 0x70, 0xEE, 0x5B, 0xEF, 0xBE, 0x09, 0xB9, 0x77,
                0x72, 0xE7, 0xB2, 0x54, 0xB7, 0x2A, 0xC7, 0x73, 0x90, 0x66, 0x20, 0x0E, 0x51, 0xED, 0xF8, 0x7C,
                0x8F, 0x2E, 0xF4, 0x12, 0xC6, 0x2B, 0x83, 0xCD, 0xAC, 0xCB, 0x3B, 0xC4, 0x4E, 0xC0, 0x69, 0x36,
                0x62, 0x02, 0xAE, 0x88, 0xFC, 0xAA, 0x42, 0x08, 0xA6, 0x45, 0x57, 0xD3, 0x9A, 0xBD, 0xE1, 0x23,
                0x8D, 0x92, 0x4A, 0x11, 0x89, 0x74, 0x6B, 0x91, 0xFB, 0xFE, 0xC9, 0x01, 0xEA, 0x1B, 0xF7, 0xCE
            ];

            public static string Unwrap(string wrappedPlSql)
            {
                Match m = Regex.Match(wrappedPlSql, @"wrapped\s*\r?\n(.*\n){19}");
                int pos = m.Index + m.Length;
                string base64Str = wrappedPlSql[pos..].TrimEnd();

                int headerSize = 22;

                byte[] data = Convert.FromBase64String(base64Str);
                for (int i = headerSize; i < data.Length; i++)
                    data[i] = substitutionTable[data[i]];

                string unwrappedPlSql = "";
                using (MemoryStream inputStream = new(data, headerSize, data.GetLength(0) - headerSize))
                using (DeflateStream decompressionStream = new(inputStream, CompressionMode.Decompress))
                using (StreamReader reader = new(decompressionStream, PlSqlUnwrapper.Encoding))
                    unwrappedPlSql = reader.ReadToEnd();

                return unwrappedPlSql.TrimEnd('\0');
            }
        }
    }
}
