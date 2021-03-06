﻿// ***********************************************************************
// Assembly         : SIPLib
// Author           : Richard Spiers
// Created          : 10-03-2012
//
// Last Modified By : Richard Spiers
// Last Modified On : 02-02-2013
// ***********************************************************************
// <copyright file="Proxy.cs">
//     Copyright (c) Richard Spiers. All rights reserved.
// </copyright>
// <summary></summary>
// ***********************************************************************
#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using SIPLib.SIP;
using SIPLib.Utils;

#endregion

namespace SIPLib.SIP
{
    /// <summary>
    /// This class is used to build a SIP proxy. It extends the Useragent class to allow correct handling of messages when acting as a SIP proxy.
    /// </summary>
    public class Proxy : UserAgent
    {
        /// <summary>
        /// Private array used to keep track of branches.
        /// </summary>
        private List<ProxyBranch> _branches = new List<ProxyBranch>();

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SIPLib.SIP.Proxy"/> class.
        /// </summary>
        /// <param name="stack">The SIP stack to use.</param>
        /// <param name="request">The incoming request to proxy.</param>
        /// <param name="server">if set to <c>true</c> [server].</param>
        public Proxy(SIPStack stack, Message request, bool server)
            : base(stack, request, server)
        {
            if (request == null) Debug.Assert(false, "Cannot create Proxy without incoming request");
        }

        /// <summary>
        /// Simply passed the request on to the receivedRequest function when acting as a proxy.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>Transaction.</returns>
        public Transaction CreateTransaction(Message request)
        {
            ReceivedRequest(null, request);
            return null;
        }

        /// <summary>
        /// Receives the SIP request, modifies headers and passes on to the stack.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="request">The request.</param>
        public override void ReceivedRequest(Transaction transaction, Message request)
        {
            try
            {
                if ((transaction != null) && Transaction != null && Transaction != transaction &&
                    request.Method.ToUpper() != "CANCEL")
                {
                    Debug.Assert(false, "Invalid transaction for received request");
                }
                Server = true;
                if (!request.Uri.Scheme.ToLower().Equals("sip"))
                {
                    SendResponse(416, "Unsupported URI scheme");
                    return;
                }

                if (request.First("Max-Forwards") != null &&
                    int.Parse(request.First("Max-Forwards").Value.ToString()) < 0)
                {
                    SendResponse(483, "Too many hops");
                    return;
                }

                if (!request.Headers["To"][0].Attributes.ContainsKey("tag") && transaction != null)
                {
                    if (Stack.FindOtherTransactions(request, transaction) != null)
                    {
                        //SendResponse(482, "Loop detected - found another transaction");
                        return;
                    }
                }

                if (request.First("Proxy-Require") != null)
                {
                    if (!request.Method.ToUpper().Contains("CANCEL") && !request.Method.ToUpper().Contains("ACK"))
                    {
                        Message response = CreateResponse(420, "Bad extension");
                        Header unsupported = request.First("Proxy-Require");
                        unsupported.Name = "Unsupported";
                        response.InsertHeader(unsupported);
                        SendResponse(unsupported);
                        return;
                    }
                }

                if (transaction != null)
                {
                    Transaction = transaction;
                }

                if (request.Method.ToUpper() == "CANCEL")
                {
                    string branch;
                    if (request.First("Via") != null && request.First("Via").Attributes.ContainsKey("branch"))
                    {
                        branch = request.First("Via").Attributes["branch"];
                    }
                    else
                    {
                        branch = Transaction.CreateBranch(request, true);
                    }
                    Transaction original = Stack.FindTransaction(Transaction.CreateId(branch, "INVITE"));
                    if (original != null)
                    {
                        if (original.State == "proceeding" || original.State == "trying")
                        {
                            original.SendResponse(original.CreateResponse(487, "Request terminated"));
                        }
                        transaction = Transaction.CreateServer(Stack, this, request, Stack.Transport,
                                                               Stack.Tag, false);
                        transaction.SendResponse(transaction.CreateResponse(200, "OK"));
                    }
                    SendCancel();
                    return;
                }

                if (string.IsNullOrEmpty(request.Uri.User) && IsLocal(request.Uri) && request.Uri.Parameters != null &&
                    request.First("Route") != null)
                {
                    Header lastRoute = request.Headers["Route"].Last();
                    request.Headers["Route"].RemoveAt(request.Headers["Route"].Count - 1);
                    request.Uri = ((Address) (lastRoute.Value)).Uri;
                }
                if (request.First("Route") != null && IsLocal(((Address) (request.First("Route").Value)).Uri))
                {
                    request.Headers["Route"].RemoveAt(0);
                    request.had_lr = true;
                }
                Stack.ReceivedRequest(this, request);
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Determines whether the specified URI is local.
        /// </summary>
        /// <param name="uri">The URI.</param>
        /// <returns><c>true</c> if the specified URI is local; otherwise, <c>false</c>.</returns>
        public bool IsLocal(SIPURI uri)
        {
            bool host = Stack.Transport.Host.ToString() == uri.Host || uri.Host == "localhost" ||
                        uri.Host == "127.0.0.1";
            bool port = false;
            if (uri.Port <= 0)
            {
                if (Stack.Transport.Port == 5060)
                {
                    port = true;
                }
            }
            port = (Stack.Transport.Port == uri.Port) || port;
            return (host && port);
        }

        /// <summary>
        /// Sends a SIP response.
        /// </summary>
        /// <param name="response">The response.</param>
        /// <param name="responseText">The response text.</param>
        /// <param name="content">The SIP body content.</param>
        /// <param name="contentType">Type of the content.</param>
        /// <param name="createDialog">Always false for a Proxy.</param>
        public override void SendResponse(object response, string responseText = "", string content = "",
                                          string contentType = "", bool createDialog = true)
        {
            if (Transaction == null)
            {
                Transaction = Transaction.CreateServer(Stack, this, Request, Stack.Transport,
                                                       Stack.Tag, false);
            }
            base.SendResponse(response, responseText, content, contentType, false);
        }

        /// <summary>
        /// Creates a SIP request.
        /// </summary>
        /// <param name="method">The SIP method.</param>
        /// <param name="dest">The dest (either Address or string[]).</param>
        /// <param name="stateless">if set to <c>true</c> [act as a stateless proxy].</param>
        /// <param name="recordRoute">if set to <c>true</c> [record the SIP route].</param>
        /// <param name="headers">The SIP headers to use.</param>
        /// <param name="route">The route.</param>
        /// <returns>Message.</returns>
        public Message CreateRequest(string method, object dest, bool stateless = false, bool recordRoute = false,
                                     Dictionary<string, List<Header>> headers = null, List<Header> route = null)
        {
            if (method != Request.Method)
            {
                Debug.Assert(false, "method in createRequest must be same as original UAS for proxy");
            }
            Message request = Request.Dup();
            if (!stateless && Transaction == null)
            {
                Transaction = Transaction.CreateServer(Stack, this, Request, Stack.Transport, Stack.Tag,
                                                       false);
            }

            if (dest.GetType() == typeof (Address))
            {
                request.Uri = ((Address) dest).Uri.Dup();
            }
            else if (dest is string[])
            {
                string[] destArray = (string[]) dest;
                string scheme = request.Uri.Scheme;
                string user = request.Uri.User;
                request.Uri = new SIPURI
                    {Scheme = scheme, User = user, Host = destArray[0], Port = int.Parse(destArray[1])};
            }
            else
            {
                Debug.Assert(false, "Dest in Proxy Create Request is not a String or Address");
                //else: request.uri = dest.dup()
            }
            if (request.First("Max-Forwards") != null)
            {
                object value = request.First("Max-Forwards").Value;
                int currentValue = int.Parse(value.ToString());
                currentValue = currentValue - 1;
                request.InsertHeader(new Header(currentValue.ToString(), "Max-Forwards"));
            }
            else request.InsertHeader(new Header("70", "Max-Forwards"));
            if (recordRoute)
            {
                Address rr = new Address(Stack.Uri.ToString());
                rr.Uri.Parameters["lr"] = null;
                rr.MustQuote = true;
                request.InsertHeader(new Header(rr.ToString(), "Record-Route"));
            }
            if (headers != null) request.Headers.Concat(headers).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            if (route != null)
            {
                route.Reverse();
                foreach (Header header in route)
                {
                    request.InsertHeader(header);
                }
            }

            Header viaHeader = Stack.CreateVia();
            viaHeader.Attributes["branch"] = Transaction.createProxyBranch(request, false);
            request.InsertHeader(viaHeader, "insert");
            return request;
        }

        /// <summary>
        /// Sends the SIP request.
        /// </summary>
        /// <param name="request">The request.</param>
        public override void SendRequest(Message request)
        {
            SIPURI target = null;
            if (request.First("Route") == null)
            {
                target = request.Uri;
            }
            else
            {
                var routes = request.Headers["Route"];
                if (routes.Count > 0)
                {
                    try
                    {
                        target = ((Address) routes[0].Value).Uri;
                        string test = target.Parameters["lr"];
                    }
                    catch (Exception)
                    {
                        routes.RemoveAt(0);
                        if (routes.Count > 0)
                        {
                            routes.Add(new Header(request.Uri.ToString(), "Route"));
                        }
                        request.Headers["Route"] = routes;
                        request.Uri = target;
                    }
                }
            }
            Stack.Sending(this, request);
            ProxyBranch branch = new ProxyBranch();

            SIPURI dest = target.Dup();
            if (target.Port <= 0)
            {
                dest.Port = 5060;
            }
            else
            {
                dest.Port = target.Port;
            }

            if (!Helpers.IsIPv4(dest.Host))
            {
                try
                {
                    IPAddress[] addresses = Dns.GetHostAddresses(dest.Host);
                    dest.Host = addresses[0].ToString();
                }
                catch (Exception)
                {
                }
            }
            if (Helpers.IsIPv4(dest.Host))
            {
                branch.RemoteCandidates = new List<SIPURI> {dest};
            }
            if (branch.RemoteCandidates == null || branch.RemoteCandidates.Count == 0)
            {
                Error(null, "Cannot resolve DNS target");
                return;
            }
            target = branch.RemoteCandidates.First();
            branch.RemoteCandidates.RemoveAt(0);
            if (!request.Method.ToUpper().Contains("ACK"))
            {
                branch.Transaction = Transaction.CreateClient(Stack, this, request, Stack.Transport, target.HostPort());
                branch.Request = request;
                _branches.Add(branch);
            }
            else
            {
                Stack.Send(request, target.HostPort());
            }
        }

        /// <summary>
        /// Retries the next candidate to send to.
        /// </summary>
        /// <param name="branch">The branch.</param>
        private void RetryNextCandidate(ProxyBranch branch)
        {
            if ((RemoteCandidates == null) || (RemoteCandidates.Count == 0))
            {
                Debug.Assert(false, String.Format("No more DNS resolved address to try"));
            }
            SIPURI target = RemoteCandidates.First();
            RemoteCandidates.RemoveAt(0);
            branch.Request.First("Via").Attributes["branch"] += "A";
            branch.Transaction = Transaction.CreateClient(Stack, this, branch.Request, Stack.Transport,
                                                          target.Host + ":" + target.Port);
        }

        /// <summary>
        /// Returns the branch ID for the specified transaction.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <returns>ProxyBranch.</returns>
        private ProxyBranch GetBranch(Transaction transaction)
        {
            return _branches.First(proxyBranch => proxyBranch.Transaction == transaction);
        }

        /// <summary>
        /// Receives the SIP response
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="response">The response.</param>
        public override void ReceivedResponse(Transaction transaction, Message response)
        {
            ProxyBranch branch = GetBranch(transaction);
            if (branch == null)
            {
                Debug.Assert(false, "Invalid transaction received " + transaction);
                return;
            }
            if (response.Is1XX() && branch.CancelRequest != null)
            {
                Transaction cancel = Transaction.CreateClient(Stack, this, branch.CancelRequest, transaction.Transport,
                                                              transaction.Remote);
                branch.CancelRequest = null;
            }
            else
            {
                if (response.IsFinal())
                {
                    branch.Response = response;
                    Stack.ReceivedResponse(this, response);
                    //SendResponseIfPossible();
                }
                else
                {
                    response.Headers["Via"].RemoveAt(0);
                    if (response.Headers["Via"].Count <= 0)
                    {
                        response.Headers.Remove("Via");
                    }
                    SendResponse(response);
                    Stack.ReceivedResponse(this, response);
                }
            }
        }

        /// <summary>
        /// Sends a SIP response if possible.
        /// </summary>
        private void SendResponseIfPossible()
        {
            List<ProxyBranch> branchesfinal =
                _branches.Where(proxyBranch => proxyBranch.Response != null && proxyBranch.Response.IsFinal()).ToList();
            List<ProxyBranch> branches2XX =
                _branches.Where(proxyBranch => proxyBranch.Response != null && proxyBranch.Response.Is2XX()).ToList();
            Message response = null;
            if (branches2XX != null && branches2XX.Count > 0)
            {
                response = branches2XX[0].Response;
            }
            else if (branchesfinal.Count == _branches.Count)
            {
                response = branchesfinal[0].Response;
            }
            if (response != null)
            {
                _branches = new List<ProxyBranch>();
                response.Headers["Via"].RemoveAt(0);
                if (response.Headers["Via"].Count == 0)
                {
                    response.Headers.Remove("Via");
                }
                SendResponse(response);
            }
        }

        /// <summary>
        /// Sends a SIP cancel request
        /// </summary>
        public override void SendCancel()
        {
            foreach (ProxyBranch proxyBranch in _branches)
            {
                proxyBranch.CancelRequest = proxyBranch.Transaction.CreateCancel();
                if (proxyBranch.Transaction.State != "trying" && proxyBranch.Transaction.State != "calling")
                {
                    if (proxyBranch.Transaction.State == "proceeding")
                    {
                        Transaction = Transaction.CreateClient(Stack, this, proxyBranch.CancelRequest,
                                                               proxyBranch.Transaction.Transport,
                                                               proxyBranch.Transaction.Remote);
                    }
                    proxyBranch.CancelRequest = null;
                }
            }
        }

        /// <summary>
        /// Handles request timeouts
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        private void TimeOut(Transaction transaction)
        {
            ProxyBranch branch = GetBranch(transaction);
            if (branch == null)
            {
                return;
            }
            branch.Transaction = null;
            if (branch.RemoteCandidates != null && branch.RemoteCandidates.Count > 0)
            {
                RetryNextCandidate(branch);
            }
            else
            {
                ReceivedResponse(null, Message.CreateResponse(408, "Request timeout", null, null, branch.Request));
            }
        }

        /// <summary>
        /// Raises an error (Service unavailable etc)
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="error">The error.</param>
        public override void Error(Transaction transaction, string error)
        {
            if (transaction == null)
            {
                Transaction = null;
                if (!Request.Method.ToUpper().Contains("ACK"))
                {
                    Message response = Message.CreateResponse(503, "Service unavailable - " + error, null, null,
                                                              Request);
                    SendResponse(response);
                    return;
                }
                Debug.Assert(false, "Warning, dropping ACK");
            }
            ProxyBranch branch = GetBranch(transaction);
            if (branch == null) return;
            Transaction = null;
            branch.Transaction = null;
            if (branch.RemoteCandidates != null && branch.RemoteCandidates.Count > 0)
            {
                RetryNextCandidate(branch);
            }
            else
            {
                ReceivedResponse(null,
                                 Message.CreateResponse(503, "Service unavailable - " + error, null, null,
                                                        branch.Request));
            }
        }
    }
}