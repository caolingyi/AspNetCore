// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Hosting.WindowsServices
{
    /// <summary>
    ///     Provides an implementation of a Windows service that hosts ASP.NET Core.
    /// </summary>
    public class WebHostService : ServiceBase
    {
        private readonly IWebHost _host;
        private bool _stopRequestedByWindows;

        /// <summary>
        /// Creates an instance of <c>WebHostService</c> which hosts the specified web application.
        /// </summary>
        /// <param name="host">The configured web host containing the web application to host in the Windows service.</param>
        public WebHostService(IWebHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// This method is not intended for direct use. Its sole purpose is to allow
        /// the service to be started by the tests.
        /// </summary>
        internal void Start() => OnStart(Array.Empty<string>());

        protected sealed override void OnStart(string[] args)
        {
            OnStarting(args);

            _host.Start();

            OnStarted();

            // Register callback for application stopping after we've
            // started the service, because otherwise we might introduce unwanted
            // race conditions.
            _host
                .Services
                .GetRequiredService<IApplicationLifetime>()
                .ApplicationStopping
                .Register(() =>
                {
                    if (!_stopRequestedByWindows)
                    {
                        Stop();
                    }
                });
        }

        protected sealed override void OnStop()
        {
            _stopRequestedByWindows = true;
            OnStopping();
            try
            {
                _host.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _host.Dispose();
                OnStopped();
            }
        }

        /// <summary>
        /// Executes before ASP.NET Core starts.
        /// </summary>
        /// <param name="args">The command line arguments passed to the service.</param>
        protected virtual void OnStarting(string[] args) { }

        /// <summary>
        /// Executes after ASP.NET Core starts.
        /// </summary>
        protected virtual void OnStarted() { }

        /// <summary>
        /// Executes before ASP.NET Core shuts down.
        /// </summary>
        protected virtual void OnStopping() { }

        /// <summary>
        /// Executes after ASP.NET Core shuts down.
        /// </summary>
        protected virtual void OnStopped() { }
    }
}
