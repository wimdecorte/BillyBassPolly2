﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Resources;
using FMdotNet__DataAPI;
using System.Net;
using Windows.System.Threading;
using Windows.Foundation.Diagnostics;
using System.IO;
using Windows.Storage;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.Media.Core;
using System.Threading;
using AdafruitClassLibrary;

// The Background Application template is documented at http://go.microsoft.com/fwlink/?LinkID=533884&clcid=0x409

namespace BillyBassPolly2
{
    public sealed class StartupTask : IBackgroundTask
    {
        BackgroundTaskDeferral _deferral;
        FMS fmserver;
        string token;
        DateTime tokenRecieved;
        LoggingChannel lc;
        private bool isRunning = false;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // 
            // TODO: Insert code to perform background work
            //
            // If you start any asynchronous methods here, prevent the task
            // from closing prematurely by using BackgroundTaskDeferral as
            // described in http://aka.ms/backgroundtaskdeferral
            //

            _deferral = taskInstance.GetDeferral();

            // set the security for fms
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            // hook into FMS from settings in the config file
            fmserver = GetFMSinstance();
            token = string.Empty;

            // log into the Events Tracing for Windows ETW
            // on the Device Portal, ETW tab, pick "Microsoft-Windows-Diagnostics-LoggingChannel" from the registered providers
            // pick level 5 and enable
            lc = new LoggingChannel("BillyBassMotorTest", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
            lc.LogMessage("Starting up.");

            // determine whether we are in testing  mode
            var resources = new ResourceLoader("Config");
            bool testing = Convert.ToBoolean(resources.GetString("testing"));
            int polling_interval_testing = Convert.ToInt16(resources.GetString(" polling_interval_testing"));
            int polling_interval = Convert.ToInt16(resources.GetString(" polling_interval"));
            lc.LogMessage("Testing = " + testing.ToString());
            if (testing == false)
            {
                // start the timer
                ThreadPoolTimer timer = ThreadPoolTimer.CreatePeriodicTimer(Timer_Tick, TimeSpan.FromMilliseconds(polling_interval));
            }
            else
            {
                // start the timer
                ThreadPoolTimer timer = ThreadPoolTimer.CreatePeriodicTimer(Timer_Tick, TimeSpan.FromSeconds(polling_interval_testing));
            }
        }


        private FMS GetFMSinstance()
        {
            var resources = new ResourceLoader("Config");
            var fm_server_address = resources.GetString("fm_server_DNS_name");
            var fm_file = resources.GetString("fm_file");
            var fm_layout = resources.GetString("fm_layout");
            var fm_account = resources.GetString("fm_account");
            var fm_pw = resources.GetString("fm_pw");


            FMS17 fms = new FMS17(fm_server_address, fm_account, fm_pw);
            fms.SetFile(fm_file);
            fms.SetLayout(fm_layout);

            return fms;
        }

        private async void Timer_Tick(ThreadPoolTimer timer)
        {
            if (isRunning == true)
            {
                lc.LogMessage("still running previous check....", LoggingLevel.Information);
                return;
            }

            await GetTaskFromFMS();
            isRunning = false;

        }


        private async System.Threading.Tasks.Task GetTaskFromFMS()
        {

            // flag that we are running
            isRunning = true;

            // record the start time
            DateTime start = DateTime.Now;

            // figure out if we need to authenticate to FMS or if we're good
            if (token == null || token == string.Empty)
            {
                lc.LogMessage("Logging into FMS.", LoggingLevel.Information);
                token = await fmserver.Authenticate();
                if (token.ToLower().Contains("error"))
                {
                    lc.LogMessage("Authentication error: " + token, LoggingLevel.Information);
                    token = string.Empty;

                    // and exit but without throwing an exception, we'll just try again on the next timer event
                    return;
                }
                else
                {
                    tokenRecieved = DateTime.Now;
                    lc.LogMessage("Token " + token + " Received at " + tokenRecieved.ToLongTimeString(), LoggingLevel.Information);
                }
            }

            if (token != string.Empty)
            {
                // how old is the token?
                TimeSpan age = start - tokenRecieved;
                lc.LogMessage("Timed run; Token age = " + age, LoggingLevel.Information);

                // query FM to see if there are any outstanding tasks
                var find = fmserver.FindRequest();

                // we only need one at most
                //find.SetStartRecord(1);
                //find.SetHowManyRecords(1);

                var request1 = find.SearchCriterium();
                request1.AddFieldSearch("flag_ready", "1");

                var foundResponse = await find.Execute();
                if (foundResponse.errorCode > 0)
                {
                    lc.LogMessage("Nothing to do. " + fmserver.lastErrorCode.ToString() + " - " + fmserver.lastErrorMessage, LoggingLevel.Error);
                    return;
                }

                // get the first FM record, we are just going to takcle that one
                var record = foundResponse.data.foundSet.records.First();

                // capture the record id, we'll need it to later update the record
                int taskId = Convert.ToInt32(record.recordId);

                // capture whatever is in the notes fields already so that we can append to it
                string notes = record.fieldsAndData.Where(pair => pair.Key == "notes").Select(pair => pair.Value).First();
                string direction = record.fieldsAndData.Where(pair => pair.Key == "motor_direction").Select(pair => pair.Value).First();
                uint speed = Convert.ToUInt16(record.fieldsAndData.Where(pair => pair.Key == "motor_speed").Select(pair => pair.Value).First());
                int motor_number = Convert.ToInt16(record.fieldsAndData.Where(pair => pair.Key == "motor_number").Select(pair => pair.Value).First());
                int how_long = Convert.ToInt16(record.fieldsAndData.Where(pair => pair.Key == "duration").Select(pair => pair.Value).First());

                /* not needed when we are just testing motors
                // download the audio file
                string url = record.fieldsAndData.Where(pair => pair.Key == "audio_file").Select(pair => pair.Value).First();
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                fmserver.SetDownloadFolder(folder.Path + @"\");

                FileInfo audioFile;
                string fmsMessage = string.Empty;
                try
                {
                    audioFile = await fmserver.DownloadFileFromContainerField(url, "play.mp3");
                    fmsMessage = fmserver.lastErrorMessage;
                    lc.LogMessage("audio file downloaded: " + audioFile.FullName, LoggingLevel.Information);
                }
                catch (Exception ex)
                {
                    lc.LogMessage("audio file not  downloaded.", LoggingLevel.Error);
                    // unflag the FM record and write the exception to notes
                    var req = fmserver.EditRequest(taskId);
                    req.AddField("flag_completed_when", DateTime.Now.ToString());
                    req.AddField("flag_ready", "");
                    req.AddField("notes", DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + " - error! " + ex.InnerException.Message + Environment.NewLine + notes);
                    // execute the request
                    var resp = await req.Execute();
                    return;
                }

                if (audioFile != null)
                {
                    try
                    {
                        lc.LogMessage("before playing audio file: " + audioFile.FullName, LoggingLevel.Information);
                        // play the audio
                        StorageFile file = await StorageFile.GetFileFromPathAsync(audioFile.FullName);

                        await PlayAudioThroughMediaPlayer(file);
                        //Thread.Sleep(2000);
                        GC.Collect();

                        // delete the file
                        lc.LogMessage("before deleting audio file.", LoggingLevel.Information);
                        try
                        {
                            await file.DeleteAsync();
                        }
                        catch (Exception ex)
                        {
                            lc.LogMessage("Could not delete audio file: " + ex.InnerException, LoggingLevel.Error);
                        }
                        lc.LogMessage("after deleting audio file.", LoggingLevel.Information);
                    }
                    catch (Exception ex)
                    {
                        fmsMessage = ex.InnerException.Message;
                    }
                }
                */

                string note = "";

                // hook into the mother hat
                MotorHat mh = new MotorHat();

                // hook into the DC motor as specified
                try
                {
                    lc.LogMessage("motor - before hooking into motor: " + motor_number.ToString(), LoggingLevel.Information);
                    MotorHat.DCMotor motor = mh.GetMotor(motor_number);
                    lc.LogMessage("motor - before setting speed for motor: " + speed.ToString(), LoggingLevel.Information);
                    motor.SetSpeed(speed);
                    lc.LogMessage("motor - before creating direction command: " + direction, LoggingLevel.Information);
                    MotorHat.DCMotor.Command which_way = (MotorHat.DCMotor.Command)Enum.Parse(typeof(MotorHat.DCMotor.Command), direction);

                    // run he motor
                    lc.LogMessage("motor - before running the motor.", LoggingLevel.Information);
                    motor.Run(which_way);
                    lc.LogMessage("motor - before pausing the motor: " + how_long.ToString() + " milliseconds", LoggingLevel.Information);
                    await Task.Delay(how_long);
                    lc.LogMessage("motor - before stopping the motor.", LoggingLevel.Information);
                    motor.Run(MotorHat.DCMotor.Command.RELEASE);
                }
                catch(Exception ex)
                {
                    note = ex.Message + " - " + ex.InnerException;
                    lc.LogMessage("Error: " + note, LoggingLevel.Error);
                }
                // write an update to FMS
                var editRequest = fmserver.EditRequest(taskId);
                editRequest.AddField("flag_ready", "");

                /*
                if (audioFile == null)
                {
                    editRequest.AddField("notes", DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + " - error! No file was downloaed" + Environment.NewLine + notes);
                }
                else if (fmsMessage != "OK")
                {
                    editRequest.AddField("notes", DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + " - error! Could not play the audio" + Environment.NewLine + notes);
                }
                else
                {
                    editRequest.AddField("notes", DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + " - Done!" + Environment.NewLine + notes);
                }
                */

                editRequest.AddField("notes", DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + " - Done! " + Environment.NewLine + note + Environment.NewLine + notes);

                // execute the edit request
                var response = await editRequest.Execute();
                if (fmserver.lastErrorCode == 0)
                {
                    lc.LogMessage("Task finished.", LoggingLevel.Information);
                }
                else
                {
                    lc.LogMessage("Task failed. " + fmserver.lastErrorCode.ToString() + " - " + fmserver.lastErrorMessage, LoggingLevel.Error);
                }



                // don't log out, re-using the token for 14 minutes or so
                //await fmserver.Logout();
                //token = string.Empty;
                if (DateTime.Now >= tokenRecieved.AddMinutes(14))
                {
                    // don't really need to log out after 14 minutes, we can just re-use the same token

                    int logoutResponse = await fmserver.Logout();
                    token = string.Empty;
                    tokenRecieved = DateTime.Now;
                    lc.LogMessage("Logging out of FMS.", LoggingLevel.Information);
                }
            }
        }


        private async Task PlayAudioThroughMediaPlayer(StorageFile file)
        {
            // uses the normal MediaPlayer but due to Raspberry firmware issues, there is a loud
            // pop at the start and end of the audio

            // works ok if you use a USB adapter for the 3.5mm output plug (headphone jack)

            MediaPlayer billy = new MediaPlayer();
            billy.AutoPlay = false;

            MediaSource source = MediaSource.CreateFromStorageFile(file);
            source.OpenOperationCompleted += MediaSource_OpenOperationCompleted;
            await source.OpenAsync();

            billy.Source = source;
            billy.Play();
            Thread.Sleep((Convert.ToInt32(source.Duration.GetValueOrDefault().TotalSeconds)) * 1000 + 500);
            lc.LogMessage("after playing audio file.", LoggingLevel.Information);
            source.Dispose();
            billy.Dispose();
        }

        private void MediaSource_OpenOperationCompleted(MediaSource sender, MediaSourceOpenOperationCompletedEventArgs args)
        {
            TimeSpan duration = sender.Duration.GetValueOrDefault();
            lc.LogMessage("Finished loading the audio file, duration = " + duration.TotalSeconds + " seconds", LoggingLevel.Information);
        }
    }
}
