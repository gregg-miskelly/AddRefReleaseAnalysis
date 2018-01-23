// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Linq;

static class TracepointAnalysis
{
    static int s_MaxCallstackDepth = 100;

    static public int MaxCallstackDepth
    {
        get { return s_MaxCallstackDepth; }
        set { s_MaxCallstackDepth = value; }
    }

    static public bool AddRefReleaseDeltasCalculated
    {
        get;
        private set;
    }

    /// <summary>
    /// Reads in a file that contains the text from the debugger's output window and analyises the tracepoint callstacks
    /// </summary>
    /// <param name="path">Path to a file containing the saved output window text</param>
    /// <param name="filter">Optional delegate to filter out callstacks that are uninteresting</param>
    /// <param name="stacks">Returns a list of all the callstacks in the output with their hit count</param>
    /// <param name="callTreeRoots">Returns the roots of the tracepoint call trees</param>
    static public void ReadFile(string path, CallstackFilter filter, out List<Callstack> stacks, out List<CallTreeNode> callTreeRoots)
    {
        int currentLine = 0;
        int currentRefCount = 0;

        Dictionary<CallstackFunction, CallTreeNode> callTreeRootMap = new Dictionary<CallstackFunction, CallTreeNode>();
        Dictionary<CallstackKey, Callstack> callstackMap = new Dictionary<CallstackKey, Callstack>();
        stacks = new List<Callstack>();
        
        StreamReader inputFile = File.OpenText(path);

        List<string> currentCallstack = null;
        Regex callstackStartLineFormat = new Regex(@"[0-9]+\:\ ?\t.+(\.dll|\.exe)\!.+");
        
        while (!inputFile.EndOfStream)
        {
            currentLine++;
            string line = inputFile.ReadLine();

            if (currentCallstack == null)
            {
                Match match = callstackStartLineFormat.Match(line);

                if (match.Success && match.Index == 0)
                {
                    string s = line.Substring(line.IndexOf('\t') + 1).Trim();

                    //Additional code to validate the debug output. Useful when figuring out why a 
                    //call stack may have gotten dropped.
                    string refcountStr = line.Substring(0, line.IndexOf(':'));
                    int newRefCount = int.Parse(refcountStr);

                    // mscordbi - ignore the 'InterlockedCompareExchange frame'
                    if (inputFile.EndOfStream)
                    {
                        Debugger.Break();
                    }
                    currentLine++;
                    line = inputFile.ReadLine();
                    s = line.Substring(line.IndexOf('\t') + 1).Trim();

                    // Is an AddRef frame?
                    if (s.EndsWith("::AddRef") || s.Contains("InterlockedIncrement"))
                    {
                        if (newRefCount != currentRefCount + 1)
                        {
                            //Debugger.Break();
                        }
                        currentRefCount++;
                    }
                    // Is a release frame?
                    else if (s.EndsWith("::Release") || s.Contains("InterlockedDecrement"))
                    {
                        if (newRefCount != currentRefCount - 1)
                        {
                            //Debugger.Break();
                        }
                        currentRefCount--;
                    }
                    else
                    {
                        Debugger.Break();
                    }

                    // Create a new call stack
                    currentCallstack = new List<string>();
                    currentCallstack.Add(s);
                    currentRefCount = newRefCount;
                }
            }
            else
            {
                if (IsOnlyWhiteSpace(line))
                {
                    CallstackFunction[] frames = CallstackFunction.GetObjects(currentCallstack);
                    CallstackKey key = new CallstackKey(frames);
                    
                    if (filter == null || filter(key))
                    {
                        Callstack.AddCallstack(stacks, callstackMap, key);
                        CallTreeNode.AddCallstack(callTreeRootMap, key);
                    }

                    currentCallstack = null;
                }
                else if (line.Length > 1 && line[0] == '\t')
                {
                    string s = line.Substring(1);

                    currentCallstack.Add(s);
                }
            }
        }

        callTreeRoots = new List<CallTreeNode>(callTreeRootMap.Values);
    }

    private static bool IsOnlyWhiteSpace(string line)
    {
        foreach (char c in line)
        {
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the AddRef/Release delta (CountOf(AddRef callstacks)-CountOf(Release callstacks)) for each function 
    /// in the call tree. Call tree display will then include this information.
    /// </summary>
    /// <param name="callTreeRoots"></param>
    public static void ComputeAddRefReleaseDeltas(List<CallTreeNode> callTreeRoots)
    {
        if (AddRefReleaseDeltasCalculated)
        {
            throw new InvalidOperationException("Deltas already calculated");
        }

        AddRefReleaseDeltasCalculated = true;

        if (callTreeRoots.Count != 2)
        {
            throw new ArgumentException("Call tree should have two root nodes - one for AddRef, one for Release");
        }

        CallTreeNode addRefNode = callTreeRoots[0];
        CallTreeNode releaseNode = callTreeRoots[1];

        if (!IsAddRefOrReleaseNode(addRefNode, "AddRef"))
        {
            Swap(ref addRefNode, ref releaseNode);
        }

        if (!IsAddRefOrReleaseNode(addRefNode, "AddRef") ||
            !IsAddRefOrReleaseNode(releaseNode, "Release"))
        {
            throw new ArgumentException("Call tree should have two root nodes - one for AddRef, one for Release");
        }

        ApplyDelta(addRefNode, 1, false, callTreeRoots);
        ApplyDelta(releaseNode, -1, false, callTreeRoots);
    }

    private static bool IsAddRefOrReleaseNode(CallTreeNode node, string name)
    {
        while (node.Function.Name.Contains("Interlocked") && node.Children.Count == 1)
            node = node.Children.Values.First<CallTreeNode>();

        return node.Function.Name.Contains(name);
    }

    private static void ApplyDelta(CallTreeNode node, int deltaValue, bool skipUpdatingRoots, List<CallTreeNode> callTreeRoots)
    {
        // Release can be called from Release. In which case we want to skip updating its delta value
        if (!skipUpdatingRoots || !IsFunctionInList(node.Function, callTreeRoots))
        {
            node.Function.ApplyDelta(deltaValue * node.HitCount);
        }
        foreach (CallTreeNode child in node.Children.Values)
        {
            ApplyDelta(child, deltaValue, true, callTreeRoots);
        }
    }

    private static bool IsFunctionInList(CallstackFunction function, List<CallTreeNode> callTreeRoots)
    {
        foreach (CallTreeNode node in callTreeRoots)
        {
            if (node.Function == function)
                return true;
        }
        return false;
    }

    private static void Swap(ref CallTreeNode node1, ref CallTreeNode node2)
    {
        CallTreeNode temp = node1;
        node1 = node2;
        node2 = temp;
    }
}

/// <summary>
/// Allows filtering the tracepoint callstacks that are interesting.
/// </summary>
/// <param name="stack">Callstack that was read from the input file</param>
/// <returns>true if the callstack should be included in the stack database / call tree roots</returns>
delegate bool CallstackFilter(CallstackKey stack);

class CallstackKey : IEquatable<CallstackKey>, IComparable<CallstackKey>
{
    public readonly CallstackFunction[] Frames;
    int m_hashCode;

    internal CallstackKey(CallstackFunction[] frames)
    {
        this.Frames = frames;

        // Using the default string array implementation of GetHashCode doesn't produce the desired effect
        foreach (CallstackFunction frame in this.Frames)
        {
            m_hashCode ^= frame.GetHashCode();
        }
    }

    public override int GetHashCode()
    {
        return m_hashCode;
    }

    public int CompareTo(CallstackKey other)
    {
        int result = this.Frames.Length - other.Frames.Length;
        if (result != 0)
            return result;

        for (int i = 0; i < this.Frames.Length; i++)
        {
            result = string.CompareOrdinal(this.Frames[i].Name, other.Frames[i].Name);
            if (result != 0)
                return result;
        }

        return 0;
    }

    // The default dictionary comparison will call this
    public bool Equals(CallstackKey other)
    {
        return (CompareTo(other) == 0);
    }

    public int IndexOf(string function)
    {
        int index = 0;

        foreach (CallstackFunction frame in this.Frames)
        {
            if (frame.Name.Contains(function))
                return index;

            index++;
        }

        return -1;
    }

    /// <summary>
    /// Returns true if any frame of the callstack contains the input text
    /// </summary>
    /// <param name="function">Text to search for</param>
    /// <returns>Result of the search</returns>
    public bool Contains(string function)
    {
        bool result = (IndexOf(function) >= 0);

        return result;
    }


    /// <summary>
    /// Reurns true if any frame of the callstack contains the callee text AND
    /// the next frame contains the caller text
    /// </summary>
    /// <param name="callee">Text to search for of the callee</param>
    /// <param name="caller">Text to search for in the caller</param>
    /// <returns>Result of the search</returns>
    public bool Contains(string callee, string caller)
    {
        int calleeIndex = IndexOf(callee);
        if (calleeIndex < 0)
            return false; // callstack does not contain the callee ext
        
        if (calleeIndex == this.Frames.Length - 1)
            return false; // callee was found on the last frame. There is no next frame

        if (!this.Frames[calleeIndex + 1].Name.Contains(caller))
            return false; // next frame is for a different caller

        return true;
    }

    /// <summary>
    /// Returns true if the callstack has either 'callee1' called by 'caller' or
    /// if it has 'callee2' called by 'caller'.
    /// </summary>
    public bool ContainsAnyPair(string callee1, string callee2, string caller)
    {
        if (Contains(callee1, caller))
            return true;
        
        if (Contains(callee2, caller))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the callstack has either 'callee1' called by 'caller' or
    /// if it has 'callee2' called by 'caller'.
    /// </summary>
    public bool ContainsAnyPair(string callee1, string callee2, string callee3, string caller)
    {
        if (Contains(callee1, caller))
            return true;

        if (Contains(callee2, caller))
            return true;

        if (Contains(callee3, caller))
            return true;

        return false;
    }
};

class Callstack : CallstackKey
{
    private int m_hitCount;
    public int HitCount
    {
        get { return m_hitCount; }
    }

    public Callstack(CallstackFunction[] frames)
        : base(frames)
    {
        m_hitCount = 1;
    }

    internal static Callstack AddCallstack(List<Callstack> stacks, Dictionary<CallstackKey, Callstack> callstackMap, CallstackKey key)
    {
        Callstack callstackObj;
        if (callstackMap.TryGetValue(key, out callstackObj))
        {
            callstackObj.m_hitCount++;
        }
        else
        {
            callstackObj = new Callstack(key.Frames);

            callstackMap.Add(callstackObj, callstackObj);
            stacks.Add(callstackObj);
        }

        return callstackObj;
    }

    public void Dump()
    {
        Console.WriteLine("Count = {0}", this.HitCount);
        foreach (CallstackFunction frame in this.Frames)
        {
            if (TracepointAnalysis.AddRefReleaseDeltasCalculated)
            {
                string deltaPrefix = frame.DeltaValue > 0 ? "+" : string.Empty;

                Console.WriteLine("{0}; Delta = {1}{2}", frame.Name, deltaPrefix, frame.DeltaValue);
            }
            else
            {
                Console.WriteLine(frame.Name);
            }
        }
        Console.WriteLine();
    }
};

[DebuggerDisplay("Function={Name}")]
public class CallstackFunction
{
    static Dictionary<string, CallstackFunction> s_dict;
    public readonly string Name;

    private int m_DeltaValue;
    public int DeltaValue
    {
        get { return m_DeltaValue; }
    }
    public void ApplyDelta(int value)
    {
        m_DeltaValue += value;
    }

    public CallstackFunction(string name)
    {
        this.Name = name;
    }

    internal static CallstackFunction[] GetObjects(List<string> frameNames)
    {
        int frameCount = frameNames.Count;

        // Stop the stack if we reach the max callstack depth
        if (frameCount > TracepointAnalysis.MaxCallstackDepth)
            frameCount = TracepointAnalysis.MaxCallstackDepth;

        //// Sometimes helpful: Stop the stack if we hit rpcrt4.dll since this marks a COM transition
        //for (int c = 0; c < frameCount; c++)
        //{
        //    if (frameNames[c].Contains("rpcrt4.dll"))
        //    {
        //        frameCount = c;
        //        break;
        //    }
        //}

        CallstackFunction[] result = new CallstackFunction[frameCount];
        for (int c = 0; c < frameCount; c++)
        {
            result[c] = GetObject(frameNames[c]);
        }

        return result;
    }

    private static CallstackFunction GetObject(string frameName)
    {
        if (s_dict == null)
            s_dict = new Dictionary<string, CallstackFunction>();

        CallstackFunction @this;
        if (!s_dict.TryGetValue(frameName, out @this))
        {
            @this = new CallstackFunction(frameName);
            s_dict.Add(frameName, @this);
        }

        return @this;
    }

    public static void DumpFunctionsWithDelta(int expectedDelta)
    {
        if (s_dict == null || !TracepointAnalysis.AddRefReleaseDeltasCalculated)
        {
            throw new InvalidOperationException("Functions not calculated");
        }

        Console.WriteLine("Functions with delta={0}:", expectedDelta);
        foreach (CallstackFunction function in s_dict.Values)
        {
            if (function.DeltaValue == expectedDelta)
            {
                Console.WriteLine(function.Name);
            }
        }
    }
};

class CallTreeNode
{
    public readonly CallstackFunction Function;
    public readonly Dictionary<CallstackFunction, CallTreeNode> Children;

    private int m_hitCount;
    public int HitCount
    {
        get { return m_hitCount; }
    }

    private CallTreeNode(CallstackFunction frame)
    {
        this.Function = frame;
        this.Children = new Dictionary<CallstackFunction, CallTreeNode>();
        this.m_hitCount = 1;
    }

    static private CallTreeNode AddNode(Dictionary<CallstackFunction, CallTreeNode> dict, CallstackFunction frame)
    {
        CallTreeNode node;
        if (dict.TryGetValue(frame, out node))
        {
            node.m_hitCount++;
            return node;
        }
        else
        {
            node = new CallTreeNode(frame);
            dict.Add(frame, node);

            return node;
        }
    }

    public void Dump(int indent)
    {
        for (int i = 0; i < indent; i++)
            Console.Write("  ");

        if (TracepointAnalysis.AddRefReleaseDeltasCalculated)
        {
            string deltaPrefix = this.Function.DeltaValue > 0 ? "+" : string.Empty;

            Console.WriteLine("{0} (Hit Count = {1}; Delta = {2}{3})", this.Function.Name, this.m_hitCount, deltaPrefix, this.Function.DeltaValue);
        }
        else
        {
            Console.WriteLine("{0} ({1})", this.Function.Name, this.m_hitCount);
        }

        CallTreeNode[] children = new CallTreeNode[this.Children.Count];
        this.Children.Values.CopyTo(children, 0);
        Array.Sort<CallTreeNode>(children, new HitCountSorter());

        foreach (CallTreeNode child in children)
        {
            child.Dump(indent + 1);
        }
    }

    internal static void AddCallstack(Dictionary<CallstackFunction, CallTreeNode> callTreeRootMap, CallstackKey key)
    {
        Dictionary<CallstackFunction, CallTreeNode> map = callTreeRootMap;

        foreach (CallstackFunction frame in key.Frames)
        {
            CallTreeNode node = AddNode(map, frame);

            map = node.Children;
        }
    }

    /// <summary>
    /// Sort the callsack nodes in decreasing order of hitcount
    /// </summary>
    internal class HitCountSorter : IComparer<CallTreeNode>
    {
        int IComparer<CallTreeNode>.Compare(CallTreeNode x, CallTreeNode y)
        {
            return y.HitCount - x.HitCount;
        }
    }
}
