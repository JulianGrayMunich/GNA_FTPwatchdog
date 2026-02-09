using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;

using GNA_CommercialLicenseValidator;

using gnaDataClasses;

using GNAgeneraltools;

namespace FTPwatchdog
{
    class Program
    {
        static int Main()
        {
            gnaTools? gnaT = null;
            string strFreezeScreen = "Yes";

            string strSystemLogsFolder = @"C:\__SystemLogs\";
            string strSystemAlarmFolder = @"C:\__SystemAlarms\";
            int exitCode = 0;

            try
            {
                #region Setting state
                Console.OutputEncoding = System.Text.Encoding.Unicode;
                if (Environment.UserInteractive && !Console.IsOutputRedirected) Console.Clear();
                int headingNo = 1;
                const string strTab1 = "     ";
                const string strTab2 = "        ";
                #endregion

                #region Instantiate core classes
                gnaT = new gnaTools();
                #endregion

                #region Read config early
                NameValueCollection config = ConfigurationManager.AppSettings;
                strFreezeScreen = CleanConfig(config["freezeScreen"]);
                if (strFreezeScreen.Length == 0) strFreezeScreen = "Yes";
                #endregion

                #region Header
                gnaT.WelcomeMessage($"GNA_FTPwatchdog {BuildInfo.BuildDateString()}");
                #endregion

                #region Config validation
                Console.WriteLine($"{headingNo++}. System Check");
                Console.Out.Flush();
                gnaT.VerifyLocalConfig();
                Console.WriteLine($"{strTab1}VerifyLocalConfig returned OK");
                Console.Out.Flush();
                #endregion

                #region License validation
                Console.WriteLine($"{headingNo++}. Validating the software license");
                string licenseCode = CleanConfig(config["LicenseCode"]);
                if (licenseCode.Length == 0)
                    throw new ConfigurationErrorsException("LicenseCode missing/empty.");
                LicenseValidator.ValidateLicense("FTPWDG", licenseCode);
                Console.WriteLine($"{strTab1}Validated");
                #endregion

                #region General variables
                Console.WriteLine($"{headingNo++}. General Variables");

                strSystemLogsFolder = CleanConfig(config["SystemLogsFolder"]);
                if (strSystemLogsFolder.Length == 0) strSystemLogsFolder = @"C:\__SystemLogs\";

                strSystemAlarmFolder = CleanConfig(config["SystemAlarmFolder"]);
                if (strSystemAlarmFolder.Length == 0) strSystemAlarmFolder = @"C:\__SystemAlarms\";

                int iTimeInterval = GetRequiredInt(config, "timeInterval", 1, 1000);

                string strProjectTitle = CleanConfig(config["ProjectTitle"]);
                if (strProjectTitle.Length == 0) strProjectTitle = "UnknownProject";
                #endregion

                #region Email settings
                Console.WriteLine($"{strTab1}Email settings");

                string strSendEmails = CleanConfig(config["sendEmails"]); // normalised

                string strEmailLogin = CleanConfig(config["EmailLogin"]);
                string strEmailPassword = CleanConfig(config["EmailPassword"]);
                string strEmailFrom = CleanConfig(config["EmailFrom"]);
                string strEmailRecipients = CleanConfig(config["EmailRecipients"]);

                EmailCredentials emailCreds = gnaT.BuildEmailCredentials(
                    strEmailLogin,
                    strEmailPassword,
                    strEmailFrom,
                    strEmailRecipients);
                #endregion

                #region SMS settings
                Console.WriteLine($"{strTab1}SMS settings");

                string strSendSMS = CleanConfig(config["sendSMS"]); // normalised

                // Support BOTH patterns:
                // - RecipientPhone1, RecipientPhone2...
                // - Phone1, Phone2...
                List<string> smsMobile = new();

                foreach (string key in config.AllKeys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    bool matchesRecipientPhone = key.StartsWith("RecipientPhone", StringComparison.OrdinalIgnoreCase);
                    bool matchesPhone = key.StartsWith("Phone", StringComparison.OrdinalIgnoreCase);

                    if (!matchesRecipientPhone && !matchesPhone)
                        continue;

                    string value = CleanConfig(config[key]);
                    if (value.Length == 0)
                        continue;

                    smsMobile.Add(value);
                }

                // De-duplicate deterministically, preserve order
                smsMobile = smsMobile
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                #endregion

                #region Main Logic

                Console.WriteLine($"{headingNo++}. Checking FTP report time");

                string ftpReportStatus = gnaT.checkReportTime("FTP", iTimeInterval);
                string stateResult = gnaTools.updateFTPStatusFile(strSystemAlarmFolder, ftpReportStatus);

                string strReportStateChange = "No";
                string strStateChangeMessage = string.Empty;

                bool stateChanged = stateResult.Equals("State_changed", StringComparison.OrdinalIgnoreCase);
                bool ftpFailed = ftpReportStatus.Equals("fail", StringComparison.OrdinalIgnoreCase);
                bool ftpSuccess = ftpReportStatus.Equals("success", StringComparison.OrdinalIgnoreCase);

                Console.WriteLine($"{strTab1}FTP state: {ftpReportStatus}");

                if (stateChanged)
                {
                    strReportStateChange = "Yes";

                    if (ftpFailed)
                        strStateChangeMessage = "FTP failed";
                    else if (ftpSuccess)
                        strStateChangeMessage = "FTP operational again";
                    else
                        strStateChangeMessage = "FTP status changed";
                }

                if (strReportStateChange.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    // ===========================
                    // Email
                    // ===========================
                    if (strSendEmails.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    {
                        string strReportStampForComms = DateTime.UtcNow.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);

                        string strSubject = $"FTP Watchdog: {strStateChangeMessage} - {strProjectTitle} ({strReportStampForComms})";

                        string strTextBody =
                            $"FTP Watchdog: {strStateChangeMessage}.\n" +
                            $"Current state: {ftpReportStatus}.\n\n" +
                            "This is an automated FTP Watchdog status issued by the monitoring system.\n" +
                            "Do not reply to this email.";

                        strTextBody = gnaT.addCopyright("FTPwatchdog", strTextBody);

                        string resultMsg = gnaT.SendEmailToRecipients(
                            emailCreds,
                            strSubject,
                            strTextBody);

                        Console.WriteLine($"{strTab2}{resultMsg}");

                        gnaT.updateSystemLogFile(
                            strSystemLogsFolder,
                            $"FTP Watchdog: {strStateChangeMessage} - {strProjectTitle} ({ftpReportStatus}) {resultMsg}");
                    }
                    else
                    {
                        Console.WriteLine($"{strTab2}Email not sent");
                    }

                    // ===========================
                    // SMS
                    // ===========================
                    if (strSendSMS.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    {
                        if (smsMobile.Count == 0)
                        {
                            Console.WriteLine($"{strTab2}SMS enabled but no RecipientPhone*/Phone* entries were found.");
                            gnaT.updateSystemLogFile(
                                strSystemLogsFolder,
                                "SMS enabled but no RecipientPhone*/Phone* entries were found.");
                        }
                        else
                        {
                            string smsMessage = $"FTP Watchdog: {strStateChangeMessage} - {strProjectTitle} ({ftpReportStatus})";

                            Console.WriteLine($"{strTab1}Send SMS message");
                            bool smsSuccess = gnaT.sendSMSArray(smsMessage, smsMobile);
                            string strMobileNumbers = string.Join(",", smsMobile);

                            if (smsSuccess)
                            {
                                Console.WriteLine($"{strTab1}Update system log file");
                                gnaT.updateSystemLogFile(strSystemLogsFolder, $"{smsMessage}: Status SMS to {strMobileNumbers}");
                            }
                            else
                            {
                                Console.WriteLine($"{strTab1}SMS array failed");
                                Console.WriteLine($"{strTab2}Update system log file");
                                gnaT.updateSystemLogFile(strSystemLogsFolder, $"{smsMessage}: SMS failed {strMobileNumbers}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"{strTab2}SMS not sent");
                    }

                    gnaT.FinishAndExit(strFreezeScreen);
                    return 0;
                }
                else
                {
                    Console.WriteLine($"{strTab1}Status unchanged");
                    gnaT.FinishAndExit(strFreezeScreen);
                    return 0;
                }

                #endregion
            }
            catch (Exception ex)
            {
                exitCode = 1;

                try { File.WriteAllText("fatal_crash.log", ex.ToString()); } catch { }

                try
                {
                    Console.WriteLine("Fatal crash:");
                    Console.WriteLine(ex);
                    Console.Out.Flush();
                }
                catch { }

                try
                {
                    if (gnaT != null)
                        gnaT.updateSystemLogFile(strSystemLogsFolder, "Fatal crash: " + ex);
                }
                catch { }

                // Deterministic non-zero exit
                return exitCode;
            }

            #region Config helpers
            static string CleanConfig(string? s) => (s ?? string.Empty).Trim().Trim('\'', '"');

            static string GetRequired(NameValueCollection cfg, string key)
            {
                string v = CleanConfig(cfg[key]);
                if (v.Length == 0)
                    throw new ConfigurationErrorsException($"Missing/empty config key '{key}'.");
                return v;
            }

            static int GetRequiredInt(NameValueCollection cfg, string key, int minValueInclusive = int.MinValue, int maxValueInclusive = int.MaxValue)
            {
                string s = GetRequired(cfg, key);

                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    throw new ConfigurationErrorsException($"Config key '{key}' is invalid (expected integer). Value='{s}'.");

                if (v < minValueInclusive || v > maxValueInclusive)
                    throw new ConfigurationErrorsException($"Config key '{key}' is out of range. Value={v}.");

                return v;
            }
            #endregion
        }
    }
}