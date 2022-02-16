﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using Castle.DynamicProxy;

namespace EasyPipes
{
    /// <summary>
    /// <see cref="NamedPipeClientStream"/> based IPC client
    /// </summary>
    public class Client
    {

        /// <summary>
        /// Default proxy class for custom proxying. Allows intercepting calls and route them through
        /// the ipc channel.
        /// </summary>
        /// <typeparam name="T">The ipc-service interface</typeparam>
        public class Proxy<T> : IInterceptor
        {
            /// <summary>
            /// The ipc client associated with this proxy
            /// </summary>
            public Client Client { get; private set; }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="c">The ipc client associated with this proxy</param>
            public Proxy(Client c)
            {
                Client = c;
            }

            /// <summary>
            /// Passes an invocation (method-call) through the IPC channel
            /// </summary>
            /// <param name="invocation">The call to pass on</param>
            public void Intercept(IInvocation invocation)
            {
                invocation.ReturnValue = Intercept(invocation.Method.Name, invocation.Arguments);
            }

            /// <summary>
            /// Passes a method-call through the IPC channel
            /// </summary>
            /// <param name="methodName">The method name (as recognized by Reflection)</param>
            /// <param name="arguments">Array of the call parameters</param>
            /// <returns>The call return</returns>
            protected object Intercept(string methodName, object[] arguments)
            {
                // build message for intercepted call
                IpcMessage msg = new IpcMessage();

                msg.Service = typeof(T).Name;
                msg.Method = methodName;
                msg.Parameters = arguments;
                
                // send message
                return Client.SendMessage(msg);
            }
        }

        /// <summary>
        /// Name of the pipe
        /// </summary>
        public string PipeName { get; private set; }
        /// <summary>
        /// Pipe data stream
        /// </summary>
        protected IpcStream Stream { get; set; }
        /// <summary>
        /// List of types registered with serializer 
        /// <seealso cref="System.Runtime.Serialization.DataContractSerializer.KnownTypes"/>
        /// </summary>
        public List<Type> KnownTypes { get; private set; }

        protected Timer timer;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="pipeName">Name of the pipe</param>
        public Client(string pipeName)
        {
            PipeName = pipeName;
            KnownTypes = new List<Type>();
        }

        /// <summary>
        /// Scans the service interface and builds proxy class
        /// </summary>
        /// <typeparam name="T">Service interface, must equal server-side</typeparam>
        /// <returns>Proxy class for remote calls</returns>
        public T GetServiceProxy<T>()
        {
            IpcStream.ScanInterfaceForTypes(typeof(T), KnownTypes);

            return (T)new ProxyGenerator().CreateInterfaceProxyWithoutTarget(typeof(T), new Proxy<T>(this));
        }

        /// <summary>
        /// Scans the service interface and registers custom proxy class
        /// </summary>
        /// <typeparam name="T">Service interface, must equal server-side</typeparam>
        /// <returns>Proxy class for remote calls</returns>
        public void RegisterServiceProxy<T>(Proxy<T> customProxy)
        {
            // check if service implements interface
            if (customProxy.GetType().GetInterface(typeof(T).Name) == null)
                throw new InvalidOperationException("Custom Proxy class does not implement service interface");

            IpcStream.ScanInterfaceForTypes(typeof(T), KnownTypes);
        }

        /// <summary>
        /// Determines the waiting time for connection to the server
        /// </summary>
        public TimeSpan ConnectionTimeout { get; set;}

        /// <summary>
        /// Connect to server. This opens a persistent connection allowing multiple remote calls
        /// until <see cref="Disconnect(bool)"/> is called.
        /// </summary>
        /// <param name="keepalive">Whether to send pings over the connection to keep it alive</param>
        /// <returns>True if succeeded, false if not</returns>
        public virtual bool Connect(bool keepalive = true)
        {
            NamedPipeClientStream source = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                if (ConnectionTimeout <= TimeSpan.Zero)
                    source.Connect();
                else
                    source.Connect((int)ConnectionTimeout.TotalMilliseconds);
            } catch(TimeoutException)
            {
                return false;
            }

            Stream = new IpcStream(source, KnownTypes);

            if(keepalive)
                StartPing();
            return true;
        }

        /// <summary>
        /// Start timer-based keep-alive pinging. Timer is set for 0.5x the timeout time.
        /// </summary>
        protected void StartPing()
        {
            timer = new Timer(
                (object state) =>
                {
                    SendMessage(new IpcMessage { StatusMsg = StatusMessage.Ping });
                },
                null,
                Server.ReadTimeOut / 2,
                Server.ReadTimeOut / 2);
        }

        /// <summary>
        /// Send the provided <see cref="IpcMessage"/> over the datastream
        /// Opens and closes a connection if not open yet
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <returns>Return value from the Remote call</returns>
        protected object SendMessage(IpcMessage message)
        {
            // if not connected, this is a single-message connection
            bool closeStream = false;
            if (Stream == null)
            {
                if (!Connect(false))
                    throw new TimeoutException("Unable to connect");
                closeStream = true;
            } else if( message.StatusMsg == StatusMessage.None )
            { // otherwise tell server to keep connection alive
                message.StatusMsg = StatusMessage.KeepAlive;
            }

            IpcMessage rv;
            lock (Stream)
            {
                Stream.WriteMessage(message);

                // don't wait for answer on keepalive-ping
                if (message.StatusMsg == StatusMessage.Ping)
                    return null;

                rv = Stream.ReadMessage();
            }
            
            if (closeStream)
                Disconnect(false);

            if (rv.Error != null)
                throw new InvalidOperationException(rv.Error);

            return rv.Return;
        }

        /// <summary>
        /// Disconnect from the server
        /// </summary>
        /// <param name="sendCloseMessage">Indicate whether to send a closing notification
        /// to the server (if you called Connect(), this should be true)</param>
        public virtual void Disconnect(bool sendCloseMessage = true)
        {
            // send close notification
            if (sendCloseMessage)
            {
                // stop keepalive ping
                timer?.Dispose();

                IpcMessage msg = new IpcMessage() { StatusMsg = StatusMessage.CloseConnection };
                Stream.WriteMessage(msg);
            }

            if (Stream != null)
                Stream.Dispose();

            Stream = null;
        }
    }
}
