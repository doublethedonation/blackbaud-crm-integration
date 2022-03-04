# Double the Donation / Blackbaud CRM Integration
The purpose of this integration is to register Blackbaud CRM donations with 360MatchPro, the industry-leading matching gift automation platform. To set up the integration:
- Create a [Smart Query in BBCRM](https://support.doublethedonation.com/portal/en/kb/articles/can-360matchpro-integrate-with-blackbaud-crm) to identify the types of gifts you want to pass into 360MatchPro.
- Run doublethedonation.exe (bin/Release) as a daemon process using a task sheduler (e.g. [Windows Task Scheduler](https://docs.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-start-page)). 

### Required and optional arguments for doublethedonation.exe
| Argument | Details |
| ------ | ------ |
| \-\-connection="SQL Server DB Connection String"  | Required |
|  \-\-privatekey=360_private_key | Required - [Learn more here](https://support.doublethedonation.com/portal/en/kb/articles/getting-started-understanding-api-keys) |
|  \-\-batchsize=500 | Optional - By default, the daemon will fetch up to 500 records at a time. | 
|  \-\-sql=custom_sql_string | Optional - By default, the daemon will use the BBCRM Smart Query with name '360MatchPro' to query new donation records.  The \-\-sql= parameter lets you pass custom SQL to the program if you prefer to not use a Smart Query.  In your customer query, make sure to order by DATECHANGED, and include "and DATECHANGED > @DATECHANGED" somewhere in your SQL WHERE statement.  However, we recommend Smart Queries since business analysts can fine tune queries without getting developers involved.| 

### PowerShell example that connects to a database using windows authentication
 .\doublethedonation.exe --privatekey=360PRIVATEKEY --connection="data source=ServerName;Initial Catalog=DatabaseName;Integrated Security=True;" --batchsize=750

### How often should we run the program?
We recommend running the program every 15 to 30 minutes.  For single, overnight runs, we recommend increasing the batch size (i.e. \-\-batchsize=N option) in order to capture all of your new donation records.

### Why do we provide this program for you to run on your system?
Many Blackbaud CRM systems operate behind a firewall with no standard REST endpoints for third party connections. Also, this program takes full advantage of BBCRM Smart Queries, which are extremely flexible and easy to configure.

