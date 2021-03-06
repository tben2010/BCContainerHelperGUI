﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Runtime.Remoting.Messaging;
using BCDockerHelper.Resources;

namespace BCDockerHelper
{
    class PowershellHelper
    {
        #region Singleton definition
        private static PowershellHelper _instance;
        public static PowershellHelper Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new PowershellHelper();
                return _instance;
            }
        }

        private PowershellHelper()
        {
            scriptInstance = PowerShell.Create();
            scriptInstance.Streams.Error.DataAdded += ErrorDataAdded;
            scriptInstance.Streams.Information.DataAdded += MessageDataAdded;
            scriptInstance.Streams.Progress.DataAdded += MessageDataAdded;
            InstallNavContainerHelper();
            InitializeAsyncScriptInstance();
        }

        private void InstallNavContainerHelper()
        {
            var pipeline = scriptInstance.Runspace.CreatePipeline();
            pipeline.Input.Write("Y");
            StringBuilder command = new StringBuilder();
            command.AppendLine("[System.Threading.Thread]::CurrentThread.CurrentCulture = \"en-US\" ");
            command.AppendLine("if (-not (Get-InstalledModule -Name navcontainerhelper -MinimumVersion \"0.6.0.15\")) {");
            command.AppendLine("Install-Module navcontainerhelper -MinimumVersion \"0.6.0.15\" -Scope AllUsers -Force -SkipPublisherCheck");
            command.AppendLine("}");
            pipeline.Commands.AddScript(command.ToString());
            pipeline.Invoke();
        }



        private void InitializeAsyncScriptInstance()
        {
            asyncScript = PowerShell.Create();
            pso = new PSDataCollection<PSObject>();
            asyncScript.Streams.Error.DataAdded += ErrorDataAdded;
            asyncScript.Streams.Information.DataAdded += MessageDataAdded;
            asyncScript.Streams.Progress.DataAdded += ProgressDataAdded;
            pso.DataAdded += MessageDataAdded;
            asyncScript.AddScript("Set-ExecutionPolicy -ExecutionPolicy Unrestricted");
            asyncScript.Invoke();
        }

        #endregion

        #region Object variables
        private PowerShell scriptInstance;
        private PowerShell asyncScript;
        private PSDataCollection<PSObject> pso;

        public event EventHandler<string> ErrorCallback;
        public event EventHandler<string> MessageCallback;
        public event EventHandler<string> StartScriptCallback;
        public event EventHandler EndScriptCallback;
        #endregion


        #region Methods
        public List<Container> GetContainers()
        {
            List<Container> containers = new List<Container>();
            scriptInstance.AddScript("docker ps -a --format \"{{.ID}};{{.Names}};{{.Status}}\"");
            var results = scriptInstance.Invoke();
            foreach (var result in results)
            {
                string[] splitLine = result.ToString().Split(';');
                Container container = new Container();
                container.ID = splitLine[0];
                container.Containername = splitLine[1];

                string status = splitLine[2];
                if (status.StartsWith("Exited"))
                {
                    status = "Stopped";
                    container.ContainerStatus = ContainerStatus.stopped;
                }
                if (status.Contains('('))
                {
                    status = status.Substring(status.IndexOf('(') + 1, status.Length - status.IndexOf('(') - 2);
                    if (status.Contains("starting"))
                    {
                        status = "starting";
                    }
                    switch (status)
                    {
                        case "healthy":
                            container.ContainerStatus = ContainerStatus.healthy;
                            break;
                        case "unhealthy":
                            container.ContainerStatus = ContainerStatus.unhealthy;
                            break;
                        case "starting":
                            container.ContainerStatus = ContainerStatus.starting;
                            status = splitLine[2];
                            break;
                        default:
                            container.ContainerStatus = ContainerStatus.unknown;
                            status = splitLine[2];
                            break;

                    }
                }
                container.ContainerStatusText = status;

                containers.Add(container);
            }
            return containers;
        }

        //docker images --format "{{.Repository}};{{.Tag}};{{.ID}};{{.Size}}"


        public async Task<bool> RestartContainer(string containername)
        {
            return await PerformPowershellAsync(String.Format("Restart-NAVContainer -containername '{0}'", containername));
        }
        public async Task<bool> StopContainer(string containername)
        {
            return await PerformPowershellAsync(String.Format("Stop-NAVContainer -containername '{0}'", containername));
        }
        public async Task<bool> StartContainer(string containername)
        {
            return await PerformPowershellAsync(String.Format("Start-NAVContainer -containername '{0}'", containername));
        }
        public async Task<bool> RemoveContainer(string containername)
        {
            return await PerformPowershellAsync(String.Format("Remove-NAVContainer -containername '{0}'", containername));
        }
        public async Task<bool> CreateContainer(string containername,string username,string password,bool includeCside,string dockerimage,bool acceptEula, string image)
        {
            StringBuilder command = new StringBuilder();
            command.AppendFormat("$credential = ([PSCredential]::new(\"{0}\", (ConvertTo-SecureString -String \"{1}\" -AsPlainText -Force))) \r\n", username, password);
            command.AppendFormat("New-NavContainer -accept_eula:{0} ", acceptEula ? "$TRUE" : "$FALSE");
            command.AppendFormat("-containername {0} ", containername);
            command.Append("-credential $credential ");
            command.Append("-auth NavUserPassword ");
            if (includeCside)  command.Append("-includeCSide ");
            command.Append("-doNotExportObjectsToText ");
            command.Append("-usessl:$false ");
            command.Append("-updateHosts ");
            command.Append("-assignPremiumPlan ");
            command.Append("-shortcuts Startmenu ");
            command.AppendFormat("-imageName {0} ", image);


            return await PerformPowershellAsync(command.ToString());            
        }

        private async Task<bool> PerformPowershellAsync(string command)
        {
            return await PerformPowershellAsync(command, true);
        }
        private async Task<bool> PerformPowershellAsync(string command,bool importNavContainerHelper)
        {
            if (asyncScript.InvocationStateInfo.State == PSInvocationState.Running)
            {
                ErrorCallback?.Invoke(this, Resources.GlobalRessources.ErrorWaitForPreviousTask);
                return false;
            }
            StringBuilder sb = new StringBuilder();
            if (importNavContainerHelper)
                sb.AppendLine("import-module navcontainerhelper");
            sb.AppendLine(command);
            asyncScript.AddScript(sb.ToString());
            StartScriptCallback?.Invoke(this, command);
            var result = asyncScript.BeginInvoke<PSObject, PSObject>(null, pso);
            await Task.Factory.FromAsync(result, x => { });
            if (asyncScript.HadErrors)
            {
                ErrorCallback?.Invoke(this, GlobalRessources.ErrorScriptCompletedWithError);
            }
            EndScriptCallback?.Invoke(this, null);
            return true;
        }

        public void ErrorDataAdded(Object sender, DataAddedEventArgs e)
        {
            PSDataCollection<ErrorRecord> dc = asyncScript.Streams.Error;
            foreach (ErrorRecord error in dc)
            {
                ErrorCallback?.Invoke(this, error.Exception.Message);
            }
            dc.Clear();
        }
        public void MessageDataAdded(Object sender, DataAddedEventArgs e)
        {

            PSDataCollection<InformationRecord> informations = (PSDataCollection<InformationRecord>)sender;
            foreach (InformationRecord information in informations)
            {
                MessageCallback?.Invoke(this, information.ToString());
            }
            informations.Clear();
        }
        public void ProgressDataAdded(Object sender, DataAddedEventArgs e)
        {
            PSDataCollection<ProgressRecord> dc = asyncScript.Streams.Progress;
            foreach (ProgressRecord progress in dc)
            {
                MessageCallback?.Invoke(this, progress.StatusDescription);
            }
            dc.Clear();
        }

        public void StopAllTasks()
        {
            asyncScript.Stop();
        }
        #endregion



    }
}
