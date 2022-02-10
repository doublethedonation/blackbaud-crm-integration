using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Dynamic;

namespace doublethedonation
{
    public class Donation
    {
        public string donation_identifier { get; set; }
        public double donation_amount { get; set; }
        public string campaign { get; set; }
        public string donor_first_name { get; set; }
        public string donor_last_name { get; set; }
        public string donor_phone { get; set; }
        public string donor_email { get; set; }

        public string donation_datetime {
            get {
                if (this.Date.HasValue)
                {
                    return this.Date.Value.ToString(DonationMigrationService.DateTimeOffsetFormatter);
                }
                else {
                    return null;
                }
            } }

        [JsonIgnore]
        public DateTimeOffset? Date { get; set; }

        public dynamic custom_fields { get; set; }

        public Donation(string donationIdentifier) {
            this.donation_identifier = donationIdentifier;
            this.campaign = "";
            this.custom_fields = new ExpandoObject();
        }
    }
}
