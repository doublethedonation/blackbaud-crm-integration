using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Data.SqlClient;

namespace doublethedonation
{
    public class DonationMigrationService
    {
        public static string DateTimeFormatter = "yyyy-MM-ddTHH\\:mm\\:ss.fff";
        public static string DateTimeOffsetFormatter = "yyyy-MM-ddTHH\\:mm\\:ss.fffzzz";

        public string Host { get; set; }
        public int BatchSize { get; set; }
        public bool VerifyTableValuedFunction { get; set; }

        private readonly string _dbConnection;
        private readonly string _privateKey;
        private readonly HttpClient _client = new HttpClient();
        private readonly HeartBeatPayload _heartbeat = new HeartBeatPayload();

        //dynamically map DTD Donation fist class fields to SQLDataReader fields
        private Dictionary<string, string> _mapping = new Dictionary<string, string>();
        private readonly Dictionary<string, IEnumerable<string>> _aliases = new Dictionary<string, IEnumerable<string>>()
            {
                { "donation_identifier", new List<string>(){ "id", "systemrecordid" }},
                { "donation_amount", new List<string>(){ "amount" }},
                { "campaign", new List<string>(){ "campaignname" }},
                { "donation_datetime", new List<string>(){ "date" }},
                { "donor_email", new List<string>(){ "constituentemailaddressesemailaddress", "email" }},
                { "donor_phone", new List<string>(){ "constituentphonesnumber", "phone" }},
                { "donor_first_name", new List<string>(){ "constituentfirstname", "firstname", "first" }},
                { "donor_last_name", new List<string>(){ "constituentlastorganizationgrouphouseholdname", "lastname", "last" }}
            };

        public DonationMigrationService(string dbConnection, string _360MatchProPrivateKey) {
            this._dbConnection = dbConnection;
            this._privateKey = _360MatchProPrivateKey;
            this._heartbeat.PrivateKey = _360MatchProPrivateKey;
            this.Host = "https://doublethedonation.com";
            this.BatchSize = 500;
            this.VerifyTableValuedFunction = true;
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        }

        private void MapFields(System.Data.SqlClient.SqlDataReader reader) {

            this._mapping = new Dictionary<string, string>();

            var dbFields = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToList();

            //prioritize first class fields over aliases
            foreach (var field in this._aliases) {
                var firstClassField = field.Key;
                var firstClassFieldInDB = dbFields.Find(s => s.ToLower().Trim() == firstClassField);
                if (firstClassFieldInDB != null)
                {
                    this._mapping.Add(firstClassField, firstClassFieldInDB);
                }
                else
                {
                    var alias = dbFields.FirstOrDefault(s => field.Value.Contains(s.ToLower().Trim()));
                    if (alias != null)
                    {
                        this._mapping.Add(firstClassField, alias);
                    }
                }
            }
        }

        private Donation ToDonation(System.Data.SqlClient.SqlDataReader reader) {

            if (this._mapping.Count() == 0) {
                this.MapFields(reader);
            }

            if (!this._mapping.ContainsKey("donation_identifier")) {
                throw new Exception("Cannot find donation_identifier in query.");
            }

            Donation donation = new Donation(Convert.ToString(reader[this._mapping["donation_identifier"]]));

            this._aliases
                .Select(kv => kv.Key)
                .Where(s => s != "donation_identifier" && s != "donation_amount" && s != "donation_datetime")
                .ToList()
                .ForEach(s => donation.GetType().GetProperty(s).SetValue(donation, Convert.ToString(reader[this._mapping[s]]), null));
            ;

            if (this._mapping.ContainsKey("donation_amount"))
            {
                try
                {
                    donation.donation_amount = Convert.ToDouble(reader[this._mapping["donation_amount"]]);
                }
                catch (Exception e) {
                    Console.WriteLine("Could not cast donation amount for donation id " + donation.donation_identifier);
                }
            }

            if (this._mapping.ContainsKey("donation_datetime"))
            {
                var data = reader[this._mapping["donation_datetime"]];

                if (data.GetType() == typeof(DateTime))
                {
                    donation.Date = (DateTimeOffset) (DateTime) data;
                }
                else if (data.GetType() == typeof(DateTimeOffset))
                {
                    donation.Date = (DateTimeOffset)data;
                }
                else
                {
                    Console.WriteLine("Could not cast donation date to DateTime or DateTimeOffset for donation id " + donation.donation_identifier);
                }
            }

            var customFieldNames = Enumerable.Range(0, reader.FieldCount)
                                             .Select(reader.GetName)
                                             .Except(this._mapping.Values)
                                             .Where(s => s != "DATECHANGED")
                                             .ToList();

            foreach (string s in customFieldNames) {
                var value = reader[s];
                if (value != null) {
                    ((IDictionary<string, object>)donation.custom_fields).Add(s, value);
                }
            }
            return donation;
        }

        public async Task<HttpResponseMessage> RegisterDonation(Donation donation, string publicKey)
        {
            if (donation.campaign == null) {
                donation.campaign = "";
            }
            string json = JsonConvert.SerializeObject(donation);
            var regex = new Regex(Regex.Escape("{"));
            string payload = regex.Replace(json, "{ \"360matchpro_public_key\": \"" + publicKey + "\", \"source\": \"BBCRM\",", 1);
            var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _client.PostAsync(this.Host + "/api/360matchpro/v1/register_donation", requestContent);
            return response;
        }

        private async Task<HttpResponseMessage> SendHeartBeat() {
            var content = JsonConvert.SerializeObject(this._heartbeat);
            var requestContent = new StringContent(content, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _client.PostAsync(this.Host + "/api/integrations/bbcrm/heartbeat", requestContent);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new Exception("Invalid Private key");
            }
            return response;
        }

        private bool IsDonationOudated(Donation donation) {
            return (donation.Date.HasValue && donation.Date.Value < DateTime.Now.AddMonths(-6));
        }

        private async Task MigrateFromhBBCRMTo360MatchPro(string sqlQuery) {

            this._heartbeat.SQL = sqlQuery;
            this._heartbeat.Status = "Initializing Daemon";
            var response = await this.SendHeartBeat();

            Console.WriteLine("Notifying Double the Donation");
            var content = await response.Content.ReadAsStringAsync();
            HeartBeatResponse json = JsonConvert.DeserializeObject<HeartBeatResponse>(content);
            string dtdPublicKey = json.publicKey;
            var dateChangedSQLParam = json.nextDateChanged;

            using (SqlConnection myConnection = new SqlConnection(this._dbConnection))
            {
                SqlCommand oCmd = new SqlCommand(sqlQuery, myConnection);
                oCmd.Parameters.AddWithValue("@DATECHANGED", dateChangedSQLParam);

                oCmd.CommandTimeout = 300;  //Throw exception for long running queries
                myConnection.Open();
                Console.WriteLine("Searching for new donation records: ");
                Console.WriteLine("      Query: " + sqlQuery);
                Console.WriteLine("      @DATECHANGED: " + dateChangedSQLParam);
                using (SqlDataReader oReader = oCmd.ExecuteReader())
                {
                    while (oReader.Read())
                    {
                        try
                        {
                            Donation donation = this.ToDonation(oReader);
                            if (this.IsDonationOudated(donation))
                            {
                                Console.WriteLine("Donation " + donation.donation_identifier + " is outdated (" + donation.donation_datetime + ")");
                            }
                            else
                            {
                                await this.RegisterDonation(donation, dtdPublicKey);
                                this._heartbeat.TotalRecordsProcessed++;

                                var dateChanged = oReader["DATECHANGED"];
                                var dateType = dateChanged.GetType();

                                if (dateType == typeof(DateTimeOffset))
                                {
                                    this._heartbeat.SetDateChanged((DateTimeOffset)dateChanged);

                                }
                                else if (dateType == typeof(DateTime))
                                {
                                    this._heartbeat.SetDateChanged((DateTime) dateChanged);
                                }
                                else
                                {
                                    throw new InvalidCastException("ERROR: DATECHANGED is not of type DateTime.");
                                }

                                Console.WriteLine("Registered: " + donation.donation_identifier);
                            }
                        }
                        catch (Exception e) {
                            Console.WriteLine("Error registering donation: " + e.Message);
                        }
                    }
                }
            }
        }

        public async Task<DonationMigrationSummary> RegisterDonations(string sqlQuery) {
            try
            {
                await this.MigrateFromhBBCRMTo360MatchPro(sqlQuery);
                this._heartbeat.Status = "BBCRM daemon complete via custom SQL";
                await this.SendHeartBeat();
            }
            catch (Exception e) {
                this._heartbeat.Error = e;
                Console.WriteLine("ERROR: " + this._heartbeat.Error.Message);
                await this.SendHeartBeat();
            }
            return new DonationMigrationSummary(this._heartbeat.TotalRecordsProcessed, this._heartbeat.Error);
        }

        public async Task<DonationMigrationSummary> RegisterDonationsViaSmartQuery()
        {
            //Get the Table-Valued function (TVFNAME) the BBCRM smart query created
            string findSmartQuery = @" SELECT TOP 1 TVFNAME, TVFPARAMETERLIST, SMARTQUERYSPEC, PRIMARYKEYFIELD, DATECHANGED
                                       FROM DBO.SMARTQUERYCATALOG
                                       WHERE LOWER(NAME) = '360matchpro'";
            SmartQueryMetaData sqMeta = new SmartQueryMetaData();
            this._heartbeat.SmartQueryMetaData = sqMeta;
            DonationMigrationSummary summary = new DonationMigrationSummary(0);

            try
            {
                using (SqlConnection connection = new SqlConnection(this._dbConnection))
                {
                    Console.WriteLine("Looking for SmartQuery with name 360MatchPro");
                    SqlCommand oCmd = new SqlCommand(findSmartQuery, connection);
                    connection.Open();
                    using (SqlDataReader oReader = oCmd.ExecuteReader())
                    {
                        while (oReader.Read())
                        {
                            sqMeta.TvfName = Convert.ToString(oReader["TVFNAME"]);
                            sqMeta.TvfParameterList = Convert.ToString(oReader["TVFPARAMETERLIST"]);
                            sqMeta.SmartQuerySpec = Convert.ToString(oReader["SMARTQUERYSPEC"]);
                            sqMeta.PrimaryKeyField = Convert.ToString(oReader["PRIMARYKEYFIELD"]);
                            sqMeta.Datechanged = Convert.ToString(oReader["DATECHANGED"]);
                        }
                    }
                }

                if (sqMeta.TvfName == null)
                {
                    throw new Exception("Smart query with name 360MatchPro not found in dbo.smartquerycatalog");
                }

                //Since TVFNAME is just a string in the database row, play it safe and verify
                //that it actually exists in the db before building the smartQuery
                if (this.VerifyTableValuedFunction)
                {
                    using (SqlConnection myConnection = new SqlConnection(this._dbConnection))
                    {
                        SqlCommand oCmd = new SqlCommand(@"SELECT SPECIFIC_NAME 
                                                           FROM INFORMATION_SCHEMA.ROUTINES
                                                           WHERE SPECIFIC_NAME = @TVFNAME"
                                                           , myConnection);
                        oCmd.Parameters.AddWithValue("@TVFNAME", sqMeta.TvfName);
                        myConnection.Open();
                        Console.WriteLine("Validating Smart Query against INFORMATION_SCHEMA.ROUTINES");
                        using (SqlDataReader oReader = oCmd.ExecuteReader())
                        {
                            if (!oReader.HasRows)
                            {
                                throw new Exception("Table Valued Function in INFORMATION_SCHEMA.ROUTINES not found. Check TVFNAME column in DBO.SMARTQUERYCATALOG for the record with NAME '360MatchPro'");
                            }
                        }
                    }
                }

                StringBuilder smartQuery = new StringBuilder();
                smartQuery.Append("SELECT * FROM ");
                smartQuery.Append(sqMeta.TvfName);
                smartQuery.Append("(@DATECHANGED, NULL, " + this.BatchSize + ")");
                summary = await this.RegisterDonations(smartQuery.ToString());
            }
            catch (Exception e)
            {
                this._heartbeat.Error = e;
                Console.WriteLine("ERROR: " + e.Message);
                await this.SendHeartBeat();
            }
            return summary;
        }

        private class HeartBeatResponse
        {
            [JsonProperty("public_key")]
            public string publicKey { get; set; }
            [JsonProperty("next_datechanged")]
            public string nextDateChanged { get; set; }
        }

        private class HeartBeatPayload
        {
            [JsonProperty("private_key")]
            public string PrivateKey { get; set; }
            [JsonProperty("datechanged")]
            public string Datechanged {
                get {
                        if (!_dateChanged.HasValue)
                        {
                            return null;
                        }
                        else if (this._noOffset) {
                            return _dateChanged.Value.ToString(DonationMigrationService.DateTimeFormatter);
                        }
                        else
                        {
                        return _dateChanged.Value.ToString(DonationMigrationService.DateTimeFormatter);
                    }
                }
            }
            private DateTimeOffset? _dateChanged;
            [JsonProperty("status")]
            public string Status { get; set; }
            [JsonProperty("sql")]
            public string SQL { get; set; }
            [JsonProperty("error")]
            public Exception Error { get; set; }
            [JsonProperty("smart_query")]
            public SmartQueryMetaData SmartQueryMetaData { get; set; }
            [JsonProperty("total_processed")]
            public int TotalRecordsProcessed { get; set; }
            private bool _noOffset = false;

            public HeartBeatPayload()
            {
                this.TotalRecordsProcessed = 0;
            }
            public void SetDateChanged(DateTimeOffset datechanged) {
                if (this._dateChanged == null || datechanged > this._dateChanged)
                {
                    this._dateChanged = datechanged;
                }
            }
            public void SetDateChanged(DateTime datechanged)
            {
                _noOffset = true;
                this.SetDateChanged((DateTimeOffset)datechanged);
            }
        }

        private class SmartQueryMetaData
        {
            public string TvfName { get; set; }
            public string TvfParameterList { get; set; }
            public string SmartQuerySpec { get; set; }
            public string PrimaryKeyField { get; set; }
            public string Datechanged { get; set; }
        }
    }

    public class DonationMigrationSummary {
        public int TotalRecordsProcessed { get; private set;}
        public Exception Error { get; private set; }
        public DonationMigrationSummary(int TotalRecordsProcessed, Exception Error) {
            this.TotalRecordsProcessed = TotalRecordsProcessed;
            this.Error = Error;
        }
        public DonationMigrationSummary(int TotalRecordsProcessed) : this(TotalRecordsProcessed, null){}
    }
}
