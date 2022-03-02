using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
    
namespace doublethedonation
{
    class Program
    {        
        static void Main(string[] args) {        
            Func<string, string> extractArg =
                argument => args.Select(s => s.Trim())
                                .Where(s => s.StartsWith("--" + argument + "="))
                                .Select(s => Regex.Replace(s, "^--" + argument + "=[\"']*", ""))
                                .Select(s => Regex.Replace(s, "[\"']+$", ""))
                                .FirstOrDefault();

            string con = extractArg("connection");
            string privateKey = extractArg("privatekey");
            string customSQL = extractArg("sql");
            string batchSize = extractArg("batchsize");
            string host = extractArg("host");

            if (args.Any(s => s.Trim().ToLower() == "--version")) {
                Console.WriteLine("version 1.0.1");
                return;                
            }

            if (con == null)
            {
                Console.WriteLine("Missing --connection=... argument");
                return;
            }
            if (privateKey == null) {
                Console.WriteLine("Missing --privatekey=... argument");
                return;
            }

            DonationMigrationService dmService = new DonationMigrationService(con, privateKey);
            if (host != null) {
                dmService.Host = host;
            }
            if (batchSize != null) {
                try
                {
                    dmService.BatchSize = Convert.ToInt32(batchSize);
                }
                catch (Exception e) {
                    Console.WriteLine("Cannot cast --batchSize to integer. The default of 500 will be used.");
                }
            }

            DonationMigrationSummary summary;            
            if (customSQL != null)
            {
                summary = dmService.RegisterDonations(customSQL).Result;
            }
            else
            {
                summary = dmService.RegisterDonationsViaSmartQuery().Result;
            }
            Console.WriteLine("Processed " + summary.TotalRecordsProcessed + " records.");
        }
    }
}
