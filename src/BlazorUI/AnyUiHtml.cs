﻿/*
Copyright (c) 2018-2021 Festo AG & Co. KG <https://www.festo.com/net/de_de/Forms/web/contact_international>
Author: Michael Hoffmeister

Copyright (c) 2019-2021 PHOENIX CONTACT GmbH & Co. KG <opensource@phoenixcontact.com>,
author: Andreas Orzelski

This source code is licensed under the Apache License 2.0 (see LICENSE.txt).

This source code may use other Open Source software components (see LICENSE.txt).
*/

// resharper disable EmptyEmbeddedStatement
// resharper disable FunctionNeverReturns
// resharper disable UnusedVariable
// resharper disable TooWideLocalVariableScope
// resharper disable EmptyConstructor

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AasxIntegrationBase;
using AasxPackageLogic;
using AasxPackageLogic.PackageCentral;
using AdminShellNS;
using BlazorUI;
using Newtonsoft.Json;

namespace AnyUi
{
    public class AnyUiHtmlEventSession
    {
        public int sessionNumber = 0;
        public bool htmlDotnetEventIn = false;
        public bool htmlDotnetEventOut = false;
        public string htmlDotnetEventType = "";
        public List<object> htmlDotnetEventInputs = new List<object>();
        public List<object> htmlDotnetEventOutputs = new List<object>();

        public bool htmlEventIn = false;
        public bool htmlEventOut = false;
        public string htmlEventType = "";
        public List<object> htmlEventInputs = new List<object>();
        public List<object> htmlEventOutputs = new List<object>();
    }
    public class AnyUiDisplayDataHtml : AnyUiDisplayDataBase
    {
        [JsonIgnore]
        public AnyUiDisplayContextHtml _context;

        public AnyUiDisplayDataHtml(AnyUiDisplayContextHtml context)
        {
            _context = context;
        }
    }
    public class AnyUiDisplayContextHtml : AnyUiContextBase
    {
        [JsonIgnore]
        public PackageCentral Packages;

        [JsonIgnore]
        public BlazorUI.Data.blazorSessionService _bi;
        public AnyUiDisplayContextHtml(BlazorUI.Data.blazorSessionService bi)
        {
            _bi = bi;
        }

        object htmlDotnetLock = new object();
        static object staticHtmlDotnetLock = new object();
        public static List<AnyUiHtmlEventSession> sessions = new List<AnyUiHtmlEventSession>();

        public static void addSession(int sessionNumber)
        {
            lock (staticHtmlDotnetLock)
            {
                var s = new AnyUiHtmlEventSession();
                s.sessionNumber = sessionNumber;
                sessions.Add(s);
            }
        }
        public static void deleteSession(int sessionNumber)
        {
            lock (staticHtmlDotnetLock)
            {
                AnyUiHtmlEventSession found = null;
                foreach (var s in sessions)
                {
                    if (s.sessionNumber == sessionNumber)
                    {
                        found = s;
                        break;
                    }
                }
                if (found != null)
                    sessions.Remove(found);
            }
        }
        public static AnyUiHtmlEventSession findSession(int sessionNumber)
        {
            AnyUiHtmlEventSession found = null;
            lock (staticHtmlDotnetLock)
            {
                foreach (var s in sessions)
                {
                    if (s.sessionNumber == sessionNumber)
                    {
                        found = s;
                        break;
                    }
                }
            }
            return found;
        }

        public static void htmlDotnetLoop()
        {
            AnyUiUIElement el;

            while (true)
            {
                // ReSharper disable InconsistentlySynchronizedField
                int i = 0;
                while (i < sessions.Count)
                {
                    var s = sessions[i];
                    if (s.htmlDotnetEventIn)
                    {
                        switch (s.htmlDotnetEventType)
                        {
                            case "setValueLambda":
                                el = (AnyUiUIElement)s.htmlDotnetEventInputs[0];
                                object o = s.htmlDotnetEventInputs[1];
                                s.htmlDotnetEventIn = false;
                                s.htmlDotnetEventInputs.Clear();
                                AnyUiLambdaActionBase ret = el.setValueLambda?.Invoke(o);
                                break;
                            case "contextMenu":
                                el = (AnyUiUIElement)s.htmlDotnetEventInputs[0];
                                AnyUiSpecialActionContextMenu cntlcm = (AnyUiSpecialActionContextMenu)
                                    s.htmlDotnetEventInputs[1];
                                s.htmlEventType = "contextMenu";
                                s.htmlEventInputs.Add(el);
                                s.htmlEventInputs.Add(cntlcm);
                                s.htmlDotnetEventIn = false;
                                s.htmlDotnetEventInputs.Clear();
                                s.htmlEventIn = true;
                                Program.signalNewData(1, s.sessionNumber); // same tree, but structure may change

                                while (!s.htmlEventOut) ;
                                int bufferedI = 0;
                                if (s.htmlEventOutputs.Count == 1)
                                {
                                    bufferedI = (int)s.htmlEventOutputs[0];
                                    var action2 = cntlcm.MenuItemLambda?.Invoke(bufferedI);
                                }
                                s.htmlEventOutputs.Clear();
                                s.htmlEventType = "";
                                s.htmlEventOut = false;
                                //// AnyUiLambdaActionBase ret = el.setValueLambda?.Invoke(o);
                                break;
                        }
                        while (s.htmlDotnetEventOut) ;
                        Program.signalNewData(2, s.sessionNumber); // build new tree
                    }
                    i++;
                }
                // ReSharper enable InconsistentlySynchronizedField
                Thread.Sleep(100);
            }
        }

        public static void setValueLambdaHtml(AnyUiUIElement el, object o)
        {
            var dc = (el.DisplayData as AnyUiDisplayDataHtml)?._context;
            if (dc != null)
            {
                var sessionNumber = dc._bi.sessionNumber;
                var found = findSession(sessionNumber);
                if (found != null)
                {
                    lock (dc.htmlDotnetLock)
                    {
                        while (found.htmlDotnetEventIn) ;
                        found.htmlEventInputs.Clear();
                        found.htmlDotnetEventType = "setValueLambda";
                        found.htmlDotnetEventInputs.Add(el);
                        found.htmlDotnetEventInputs.Add(o);
                        found.htmlDotnetEventIn = true;
                    }
                }
            }
        }

        public static void specialActionContextMenuHtml(AnyUiUIElement el, AnyUiSpecialActionContextMenu cntlcm)
        {
            var dc = (el.DisplayData as AnyUiDisplayDataHtml)?._context;
            if (dc != null)
            {
                var sessionNumber = dc._bi.sessionNumber;
                var found = findSession(sessionNumber);
                if (found != null)
                {
                    lock (dc.htmlDotnetLock)
                    {
                        while (found.htmlDotnetEventIn) ;
                        found.htmlEventInputs.Clear();
                        found.htmlDotnetEventType = "contextMenu";
                        found.htmlDotnetEventInputs.Add(el);
                        found.htmlDotnetEventInputs.Add(cntlcm);
                        found.htmlDotnetEventIn = true;
                    }
                }
            }
        }

        /// <summary>
        /// Show MessageBoxFlyout with contents
        /// </summary>
        /// <param name="message">Message on the main screen</param>
        /// <param name="caption">Caption string (title)</param>
        /// <param name="buttons">Buttons according to WPF standard messagebox</param>
        /// <param name="image">Image according to WPF standard messagebox</param>
        /// <returns></returns>
        public override AnyUiMessageBoxResult MessageBoxFlyoutShow(
            string message, string caption, AnyUiMessageBoxButton buttons, AnyUiMessageBoxImage image)
        {
            AnyUiHtmlEventSession found = null;
            lock (htmlDotnetLock)
            {
                foreach (var s in sessions)
                {
                    if (_bi.sessionNumber == s.sessionNumber)
                    {
                        found = s;
                        break;
                    }
                }
            }

            AnyUiMessageBoxResult r = AnyUiMessageBoxResult.None;
            if (found != null)
            {
                found.htmlEventInputs.Clear();
                found.htmlEventType = "MessageBoxFlyoutShow";
                found.htmlEventInputs.Add(message);
                found.htmlEventInputs.Add(caption);
                found.htmlEventInputs.Add(buttons);
                found.htmlEventIn = true;
                Program.signalNewData(2, found.sessionNumber); // build new tree

                while (!found.htmlEventOut) ;
                if (found.htmlEventOutputs.Count == 1)
                    r = (AnyUiMessageBoxResult)found.htmlEventOutputs[0];

                found.htmlEventType = "";
                found.htmlEventOutputs.Clear();
                found.htmlEventOut = false;
                found.htmlEventInputs.Clear();
                found.htmlDotnetEventIn = false;
            }

            return r;
        }

        /// <summary>
        /// Shows specified dialogue hardware-independent. The technology implementation will show the
        /// dialogue based on the type of provided <c>dialogueData</c>. 
        /// Modal dialogue: this function will block, until user ends dialogue.
        /// </summary>
        /// <param name="dialogueData"></param>
        /// <returns>If the dialogue was end with "OK" or similar success.</returns>
        public override bool StartFlyoverModal(AnyUiDialogueDataBase dialogueData)
        {
            // access
            if (dialogueData == null)
                return false;

            // make sure to reset
            dialogueData.Result = false;

            AnyUiHtmlEventSession found = null;
            lock (htmlDotnetLock)
            {
                foreach (var s in sessions)
                {
                    if (_bi.sessionNumber == s.sessionNumber)
                    {
                        found = s;
                        break;
                    }
                }
            }

            if (found != null)
            {
                found.htmlEventInputs.Clear();
                found.htmlEventType = "StartFlyoverModal";
                found.htmlEventInputs.Add(dialogueData);

                found.htmlEventIn = true;
                Program.signalNewData(2, found.sessionNumber); // build new tree

                while (!found.htmlEventOut) ;
                if (dialogueData is AnyUiDialogueDataTextEditor ddte)
                {
                    if (found.htmlEventOutputs.Count == 2)
                    {
                        ddte.Text = (string)found.htmlEventOutputs[0];
                        ddte.Result = (bool)found.htmlEventOutputs[1];
                    }
                }
                if (dialogueData is AnyUiDialogueDataSelectFromList ddsfl)
                {
                    ddsfl.Result = false;
                    if (found.htmlEventOutputs.Count == 1)
                    {
                        int iDdsfl = (int)found.htmlEventOutputs[0];
                        ddsfl.Result = true;
                        ddsfl.ResultIndex = iDdsfl;
                        ddsfl.ResultItem = ddsfl.ListOfItems[iDdsfl];
                    }
                }
                found.htmlEventType = "";
                found.htmlEventOutputs.Clear();
                found.htmlEventOut = false;
                found.htmlEventInputs.Clear();
                found.htmlDotnetEventIn = false;
            }
            // result
            return dialogueData.Result;
        }
    }
}
