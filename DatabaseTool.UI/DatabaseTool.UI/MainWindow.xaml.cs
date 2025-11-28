using Microsoft.Win32;
using MySql.Data.MySqlClient;
using System;
using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DatabaseMigratieApp
{
    public partial class MainWindow : Window
    {
        private bool isMigrationRunning = false;

        // MySQL connection string - PAS DIT AAN!
        private string mysqlConnectionString = "Server=localhost;Database=testdb;Uid=root;Pwd=;";

        public MainWindow()
        {
            InitializeComponent();
            UpdateUIState();
            TestMySQLConnection();
        }

        private async void TestMySQLConnection()
        {
            try
            {
                using (var connection = new MySqlConnection(mysqlConnectionString))
                {
                    await connection.OpenAsync();
                    txtValidation.Text = "✓ MySQL verbinding succesvol";
                    txtValidation.Foreground = System.Windows.Media.Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                txtValidation.Text = $"✗ MySQL verbinding mislukt: {ex.Message}";
                txtValidation.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Access Databases (*.mdb, *.accdb)|*.mdb;*.accdb",
                Title = "Selecteer Access Bestand"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                txtFilePath.Text = openFileDialog.FileName;
                ValidateAccessFile(openFileDialog.FileName);
            }
        }

        private void ValidateAccessFile(string filePath)
        {
            try
            {
                // Reset UI
                txtValidation.Text = "Bezig met valideren...";
                txtValidation.Foreground = System.Windows.Media.Brushes.Black;
                btnStartMigration.IsEnabled = false;

                // Stap 2: Controleer of het bestand een Access bestand is
                if (!File.Exists(filePath))
                {
                    ShowValidationError("Bestand niet gevonden.");
                    return;
                }

                string extension = Path.GetExtension(filePath).ToLower();
                if (extension != ".mdb" && extension != ".accdb")
                {
                    ShowValidationError("Ongeldig bestandstype. Selecteer een .mdb of .accdb bestand.");
                    return;
                }

                // Test de Access database verbinding
                string accessConnectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};";

                using (var connection = new OleDbConnection(accessConnectionString))
                {
                    connection.Open();

                    // Haal alleen gebruikers tabellen op (geen systeemtabellen)
                    var tables = connection.GetSchema("Tables");
                    int userTableCount = 0;
                    string tableNames = "";

                    foreach (DataRow table in tables.Rows)
                    {
                        string tableName = table["TABLE_NAME"].ToString();
                        string tableType = table["TABLE_TYPE"].ToString();

                        // Filter alleen gebruikers tabellen
                        if (tableType == "TABLE" &&
                            !tableName.StartsWith("MSys") &&
                            !tableName.StartsWith("~") &&
                            !tableName.StartsWith("_"))
                        {
                            userTableCount++;
                            tableNames += $"- {tableName}\n";
                        }
                    }

                    if (userTableCount == 0)
                    {
                        ShowValidationError("Geen gebruikers tabellen gevonden in Access database.");
                        return;
                    }

                    txtValidation.Text = $"✓ Access bestand gevalideerd.\n{userTableCount} gebruikers tabellen gevonden:\n{tableNames}";
                    txtValidation.Foreground = System.Windows.Media.Brushes.Green;
                    btnStartMigration.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                ShowValidationError($"Fout bij validatie: {ex.Message}\n\nControleer:\n- Is Microsoft Access Database Engine geïnstalleerd?\n- Is het bestand niet corrupt?\n- Heeft de applicatie leesrechten?");
            }
        }

        private void ShowValidationError(string message)
        {
            txtValidation.Text = $"✗ {message}";
            txtValidation.Foreground = System.Windows.Media.Brushes.Red;
            btnStartMigration.IsEnabled = false;
        }

        private async void BtnStartMigration_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtFilePath.Text) || !File.Exists(txtFilePath.Text))
            {
                MessageBox.Show("Selecteer eerst een geldig Access bestand.", "Fout",
                              MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await StartMigrationAsync(txtFilePath.Text);
        }

        private async Task StartMigrationAsync(string accessFilePath)
        {
            isMigrationRunning = true;
            UpdateUIState();

            // Reset resultaten
            txtResult.Text = "";
            txtProgress.Text = "";
            progressBar.Value = 0;
            btnRetry.Visibility = Visibility.Collapsed;

            try
            {
                // Stap 4: Toon laadscherm (progress bar)
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 10;
                txtProgress.Text = "Database connectie controleren...";

                // Voer de migratie uit
                bool success = await MigrateAccessToMySQL(accessFilePath);

                // Stap 5: Toon resultaat
                if (success)
                {
                    progressBar.Value = 100;
                    txtResult.Text = "✓ Migratie succesvol voltooid!";
                    txtResult.Foreground = System.Windows.Media.Brushes.Green;
                    MessageBox.Show("Data migratie is succesvol afgerond!", "Succes",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    HandleMigrationError("Migratie is mislukt.");
                }
            }
            catch (Exception ex)
            {
                HandleMigrationError($"Migratie mislukt: {ex.Message}");
            }
            finally
            {
                isMigrationRunning = false;
                UpdateUIState();
                if (progressBar.Value != 100)
                    progressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<bool> MigrateAccessToMySQL(string accessFilePath)
        {
            string accessConnectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={accessFilePath};";

            try
            {
                using (var accessConnection = new OleDbConnection(accessConnectionString))
                using (var mysqlConnection = new MySqlConnection(mysqlConnectionString))
                {
                    await accessConnection.OpenAsync();
                    await mysqlConnection.OpenAsync();

                    // Haal alleen gebruikers tabellen op
                    var tablesSchema = accessConnection.GetSchema("Tables");
                    var allTables = tablesSchema.Select("TABLE_TYPE = 'TABLE'");

                    // Filter systeemtabellen eruit
                    var userTables = allTables.Where(table =>
                        !table["TABLE_NAME"].ToString().StartsWith("MSys") &&
                        !table["TABLE_NAME"].ToString().StartsWith("~") &&
                        !table["TABLE_NAME"].ToString().StartsWith("_")).ToArray();

                    int totalTables = userTables.Length;

                    if (totalTables == 0)
                    {
                        throw new Exception("Geen gebruikers tabellen gevonden in de database.");
                    }

                    int currentTable = 0;
                    string migrationResult = "=== MIGRATIE RAPPORT ===\n\n";
                    int totalRowsMigrated = 0;

                    progressBar.Value = 20;
                    txtProgress.Text = $"Starten met migratie van {totalTables} tabellen...";

                    foreach (DataRow tableRow in userTables)
                    {
                        string tableName = tableRow["TABLE_NAME"].ToString();
                        currentTable++;

                        // Update voortgang
                        int progress = 20 + (int)((double)currentTable / totalTables * 70);
                        progressBar.Value = progress;
                        txtProgress.Text = $"Bezig met migreren tabel {currentTable}/{totalTables}: {tableName}";

                        // Migreer tabel naar MySQL
                        int rowsMigrated = await MigrateTable(accessConnection, mysqlConnection, tableName);
                        totalRowsMigrated += rowsMigrated;

                        migrationResult += $"✅ {tableName}: {rowsMigrated} rijen gemigreerd\n";

                        await Task.Delay(100);
                    }

                    // Toon migratie resultaat
                    txtProgress.Text = migrationResult + $"\n✓ Migratie voltooid!\n{totalTables} tabellen\n{totalRowsMigrated} rijen totaal";
                    return true;
                }
            }
            catch (MySqlException ex)
            {
                throw new Exception($"MySQL database fout: {ex.Message}");
            }
            catch (Exception ex) when (ex.Message.Contains("Microsoft.ACE.OLEDB") || ex.Message.Contains("provider"))
            {
                throw new Exception($"Access database fout: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Migratie fout: {ex.Message}");
            }
        }

        private async Task<int> MigrateTable(OleDbConnection accessConn, MySqlConnection mysqlConn, string tableName)
        {
            int rowsMigrated = 0;

            try
            {
                // Controleer of tabel bestaat in MySQL, zo niet maak aan
                if (!await TableExistsInMySQL(mysqlConn, tableName))
                {
                    await CreateTableInMySQL(accessConn, mysqlConn, tableName);
                }
                else
                {
                    // Wis bestaande data (optioneel - pas aan naar je behoefte)
                    await ClearTableInMySQL(mysqlConn, tableName);
                }

                // Lees data uit Access
                using (var accessCommand = new OleDbCommand($"SELECT * FROM [{tableName}]", accessConn))
                using (var reader = accessCommand.ExecuteReader()) // synchroon
                {
                    while (reader.Read())
                    {
                        if (await InsertRowIntoMySQL(mysqlConn, tableName, reader)) // geen async hier
                        {
                            rowsMigrated++;
                        }
                    }
                }



                return rowsMigrated;
            }
            catch (Exception ex)
            {
                throw new Exception($"Fout bij migreren tabel {tableName}: {ex.Message}");
            }
        }

        private async Task<bool> TableExistsInMySQL(MySqlConnection connection, string tableName)
        {
            string query = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @TableName";

            using (var command = new MySqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TableName", tableName);
                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt32(result) > 0;
            }
        }

        private async Task CreateTableInMySQL(OleDbConnection accessConn, MySqlConnection mysqlConn, string tableName)
        {
            // Haal kolom informatie op uit Access
            using (var accessCommand = new OleDbCommand($"SELECT TOP 1 * FROM [{tableName}]", accessConn))
            using (var reader = await accessCommand.ExecuteReaderAsync(CommandBehavior.SchemaOnly))
            {
                var schemaTable = reader.GetSchemaTable();

                string createTableQuery = $"CREATE TABLE `{tableName}` (";
                string columns = "";

                foreach (DataRow row in schemaTable.Rows)
                {
                    string columnName = row["ColumnName"].ToString();

                    // Gebruik DATA_TYPE en converteer naar MySQL type
                    Type type = (Type)row["DataType"];
                    string dataType = ConvertAccessTypeToMySQL(type);

                    if (!string.IsNullOrEmpty(columns))
                        columns += ", ";

                    columns += $"`{columnName}` {dataType}";

                    // Voeg PRIMARY KEY toe als het ID kolom is
                    if (columnName.ToLower() == "id")
                        columns += " PRIMARY KEY AUTO_INCREMENT";
                }


                createTableQuery += columns + ")";

                using (var command = new MySqlCommand(createTableQuery, mysqlConn))
                {
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private string ConvertAccessTypeToMySQL(Type accessType)
        {
            string typeName = accessType.Name.ToLower();
            switch (typeName)
            {
                case "int16":
                case "int32":
                    return "INT";
                case "single":
                case "double":
                case "decimal":
                    return "DECIMAL(10,2)";
                case "datetime":
                    return "DATETIME";
                case "boolean":
                    return "BOOLEAN";
                case "string":
                default:
                    return "TEXT";
            }
        }



        private async Task ClearTableInMySQL(MySqlConnection connection, string tableName)
        {
            string query = $"DELETE FROM `{tableName}`";
            using (var command = new MySqlCommand(query, connection))
            {
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task<bool> InsertRowIntoMySQL(MySqlConnection connection, string tableName, OleDbDataReader reader)
        {
            try
            {
                string columns = "";
                string values = "";

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string columnName = reader.GetName(i);

                    if (!string.IsNullOrEmpty(columns))
                    {
                        columns += ", ";
                        values += ", ";
                    }

                    columns += $"`{columnName}`";
                    values += $"@param{i}";
                }

                string insertQuery = $"INSERT INTO `{tableName}` ({columns}) VALUES ({values})";

                using (var command = new MySqlCommand(insertQuery, connection))
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                        command.Parameters.AddWithValue($"@param{i}", value);
                    }

                    await command.ExecuteNonQueryAsync();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HandleMigrationError(string errorMessage)
        {
            progressBar.Value = 0;
            txtResult.Text = $"✗ {errorMessage}";
            txtResult.Foreground = System.Windows.Media.Brushes.Red;
            txtProgress.Text = "Migratie mislukt - zie foutmelding";
            btnRetry.Visibility = Visibility.Visible;

            MessageBox.Show(errorMessage, "Migratie Mislukt",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            // Reset UI voor nieuwe poging
            btnRetry.Visibility = Visibility.Collapsed;
            txtResult.Text = "";
            txtProgress.Text = "";
            progressBar.Value = 0;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (isMigrationRunning)
            {
                var result = MessageBox.Show("Migratie is bezig. Weet u zeker dat u wilt annuleren?",
                                           "Annuleren",
                                           MessageBoxButton.YesNo,
                                           MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Application.Current.Shutdown();
                }
            }
            else
            {
                Application.Current.Shutdown();
            }
        }

        private void UpdateUIState()
        {
            btnBrowse.IsEnabled = !isMigrationRunning;
            btnStartMigration.IsEnabled = !isMigrationRunning && btnStartMigration.IsEnabled;
            btnCancel.Content = isMigrationRunning ? "Annuleren Migratie" : "Afsluiten";
        }
    }
}