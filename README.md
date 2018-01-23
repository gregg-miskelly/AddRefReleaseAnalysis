# AddRef Release Analysis
This project provides an example program that can help to track down the source of a reference leak (or over-release) when dealing with an object that is frequently AddRef'ed and Released so it can be very painful to go through AddRef/Release calls by hand.

# How to use this project

1. Find the object that you want to trace. If you are dealing with an over-release, this is the object where the ref count goes to zero even though it is still used. If you are dealing with a leaked object, you want to try to find the "root" leaked object by looking at all the leaked objects, and finding the object where if that object was released the other leaked objects would be leaked as well.
2. Try to narrow down the repro steps. The longer the repro steps the more stacks you will need to deal with.
3. Set a breakpoint/breakpoints on where the reference count changes. You can do this with a data breakpoint on the ref count variable, or you can make a conditional breakpoint on the AddRef/Release function.
4. Change this breakpoint into a tracepoint (a breakpoint that prints information when it hits and then continues execution) by right clicking on the breakpoint and setting the 'Action' to '{\*(DWORD\*)0x123456}:$CALLSTACK' where 0x123456 should be replaced with the address of the ref count variable.
5. Run the repro steps.
6. Save the text of the debug pane of the output window to a file.
7. Run this example program over the log file. You may need to tweak the analysis for any small differences in the tracepoint format. For example, if your 'AddRef' and 'Release' functions are named differently.
8. Change the code in ExampleProgram.cs to explore your data. For example, you can:
    * Bucketize each call stack
    * See the call tree
    * Dump out functions that have a delta (number of AddRef calls- number of Release calls)

One analysis technique I would suggest is to understand that any function near the leaf should either AddRef each time they are called (ex: AddRef, QueryInterface, CComPtr::CComPtr, CComPtr::CopyTo), Release every time they are called (ex: Release, CComPtr::~CComPtr), or be neurtal (ex: method that aquires a reference and then releases it on exit). So one useful technique is to limit the stack depth to a fairly small amount and then look at all methods to see if they have the right delta.
