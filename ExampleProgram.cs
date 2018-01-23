// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

class ExampleProgram
{
    static void Main(string[] args)
    {
        List<Callstack> stacks;
        List<CallTreeNode> callTreeRoots;

        CallstackFilter filter = delegate(CallstackKey key)
        {
            // This function can be used to remove functions that you think are unrelated to the leak.
            // Example:
            //  if (key.Contains("FunctionWithBalancedAddRefsAndReleases"))
            //      return false;

            return true;
        };

        TracepointAnalysis.MaxCallstackDepth = 50;

        TracepointAnalysis.ReadFile(@"C:\Users\greggm\Desktop\leakdiag.txt", filter, out stacks, out callTreeRoots);

        TracepointAnalysis.ComputeAddRefReleaseDeltas(callTreeRoots);
        
        int totalDelta = callTreeRoots[0].Function.DeltaValue + callTreeRoots[1].Function.DeltaValue;
        const int expectedDelta = 2;

        if (totalDelta != expectedDelta)
        {
            if (totalDelta <= 0)
                Console.WriteLine("AddRef/Release problem went away");
            else
                Console.WriteLine("AddRef/Release problem is bigger than expected");

            return;
        }

        CallstackFunction.DumpFunctionsWithDelta(2);
        DumpStacks(stacks);
        //DumpCallTree(callTreeRoots);
    }

    private static void DumpCallTree(List<CallTreeNode> roots)
    {
        foreach (CallTreeNode root in roots)
        {
            root.Dump(0);
        }
    }

    private static void DumpStacks(List<Callstack> list)
    {
        // Sort the stacks by their hit count
        Comparison<Callstack> comparison = delegate(Callstack a, Callstack b)
        {
            return a.HitCount - b.HitCount;
        };
        list.Sort(comparison);

        foreach (Callstack item in list)
        {
            item.Dump();
        }
    }
}

