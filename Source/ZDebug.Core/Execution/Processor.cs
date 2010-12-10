﻿using System;
using System.Collections.Generic;
using ZDebug.Core.Basics;
using ZDebug.Core.Instructions;
using ZDebug.Core.Text;
using ZDebug.Core.Utilities;

namespace ZDebug.Core.Execution
{
    public sealed partial class Processor : IExecutionContext
    {
        private readonly Story story;
        private readonly IMemoryReader reader;
        private readonly InstructionReader instructions;
        private readonly Stack<StackFrame> callStack;
        private readonly OutputStreams outputStreams;
        private IScreen screen = new NullScreen();
        private Random random = new Random();

        private Instruction executingInstruction;

        internal Processor(Story story)
        {
            this.story = story;

            this.callStack = new Stack<StackFrame>();
            this.outputStreams = new OutputStreams(story);

            // create "call" to main routine
            var mainRoutineAddress = story.Memory.ReadMainRoutineAddress();
            this.reader = story.Memory.CreateReader(mainRoutineAddress);
            this.instructions = reader.AsInstructionReader(story.Version);

            var localCount = reader.NextByte();
            var locals = ArrayEx.Create(localCount, i => Value.Zero);

            callStack.Push(
                new StackFrame(
                    mainRoutineAddress,
                    arguments: ArrayEx.Empty<Value>(),
                    locals: locals,
                    returnAddress: null,
                    storeVariable: null));
        }

        private Value ReadVariable(Variable variable, bool indirect = false)
        {
            switch (variable.Kind)
            {
                case VariableKind.Stack:
                    if (indirect)
                    {
                        return CurrentFrame.PeekValue();
                    }
                    else
                    {
                        return CurrentFrame.PopValue();
                    }

                case VariableKind.Local:
                    return CurrentFrame.Locals[variable.Index];

                case VariableKind.Global:
                    return story.GlobalVariablesTable[variable.Index];

                default:
                    throw new InvalidOperationException();
            }
        }

        private void WriteVariable(Variable variable, Value value, bool indirect = false)
        {
            switch (variable.Kind)
            {
                case VariableKind.Stack:
                    if (indirect)
                    {
                        CurrentFrame.PopValue();
                    }

                    CurrentFrame.PushValue(value);
                    break;

                case VariableKind.Local:
                    var oldValue = CurrentFrame.Locals[variable.Index];
                    CurrentFrame.SetLocal(variable.Index, value);
                    OnLocalVariableChanged(variable.Index, oldValue, value);
                    break;

                case VariableKind.Global:
                    story.GlobalVariablesTable[variable.Index] = value;
                    break;

                default:
                    throw new InvalidOperationException();
            }
        }

        private Value GetOperandValue(Operand operand)
        {
            switch (operand.Kind)
            {
                case OperandKind.LargeConstant:
                    return operand.AsLargeConstant();
                case OperandKind.SmallConstant:
                    return operand.AsSmallConstant();
                case OperandKind.Variable:
                    return ReadVariable(operand.AsVariable());
                default:
                    throw new InvalidOperationException();
            }
        }

        private void WriteStoreVariable(Variable storeVariable, Value value)
        {
            if (storeVariable != null)
            {
                WriteVariable(storeVariable, value);
            }
        }

        private void Call(int address, Operand[] operands, Variable storeVariable)
        {
            if (address < 0)
            {
                throw new ArgumentOutOfRangeException("address");
            }

            // NOTE: argument values must be retrieved in case they manipulate the stack
            var argValues = operands != null
                ? operands.Select(GetOperandValue)
                : ArrayEx.Empty<Value>();

            if (address == 0)
            {
                // SPECIAL CASE: A routine call to packed address 0 is legal: it does nothing and returns false (0). Otherwise it is
                // illegal to call a packed address where no routine is present.

                // If there is a store variable, write 0 to it.
                WriteStoreVariable(storeVariable, Value.Zero);
            }
            else
            {
                story.RoutineTable.Add(address);

                var returnAddress = reader.Address;
                reader.Address = address;

                // read locals
                var localCount = reader.NextByte();
                var locals = story.Version <= 4
                    ? ArrayEx.Create(localCount, _ => Value.Number(reader.NextWord()))
                    : ArrayEx.Create(localCount, _ => Value.Zero);

                var numberToCopy = Math.Min(argValues.Length, locals.Length);
                Array.Copy(argValues, 0, locals, 0, numberToCopy);

                var oldFrame = CurrentFrame;
                var newFrame = new StackFrame(address, argValues, locals, returnAddress, storeVariable);

                callStack.Push(newFrame);

                OnEnterFrame(oldFrame, newFrame);
            }
        }

        private void Jump(short offset)
        {
            reader.Address += offset - 2;
        }

        private void Jump(Branch branch)
        {
            if (branch.Kind == BranchKind.Address)
            {
                reader.Address += branch.Offset - 2;
            }
            else if (branch.Kind == BranchKind.RFalse)
            {
                Return(Value.Zero);
            }
            else if (branch.Kind == BranchKind.RTrue)
            {
                Return(Value.One);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private void Return(Value value)
        {
            var oldFrame = callStack.Pop();

            OnExitFrame(oldFrame, CurrentFrame);

            reader.Address = oldFrame.ReturnAddress;

            WriteStoreVariable(oldFrame.StoreVariable, value);
        }

        private void WriteProperty(int objNum, int propNum, ushort value)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            var prop = obj.PropertyTable.GetByNumber(propNum);

            if (prop.DataLength == 2)
            {
                story.Memory.WriteWord(prop.DataAddress, value);
            }
            else if (prop.DataLength == 1)
            {
                story.Memory.WriteByte(prop.DataAddress, (byte)(value & 0x00ff));
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private bool HasAttribute(int objNum, int attrNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);

            return obj.HasAttribute(attrNum);
        }

        public void Step()
        {
            var oldPC = reader.Address;
            executingInstruction = instructions.NextInstruction();
            OnStepping(oldPC);

            executingInstruction.Opcode.Execute(executingInstruction, this);

            var newPC = reader.Address;
            OnStepped(oldPC, newPC);
            executingInstruction = null;
        }

        public void RegisterScreen(IScreen screen)
        {
            if (screen == null)
            {
                throw new ArgumentNullException("screen");
            }

            this.screen = screen;
            this.outputStreams.RegisterScreen(screen);
        }

        public StackFrame CurrentFrame
        {
            get { return callStack.Peek(); }
        }

        public int PC
        {
            get { return reader.Address; }
        }

        /// <summary>
        /// The Instruction that is being executed (only valid during a step).
        /// </summary>
        public Instruction ExecutingInstruction
        {
            get { return executingInstruction; }
        }

        private void OnStepping(int oldPC)
        {
            var handler = Stepping;
            if (handler != null)
            {
                handler(this, new ProcessorSteppingEventArgs(oldPC));
            }
        }

        private void OnStepped(int oldPC, int newPC)
        {
            var handler = Stepped;
            if (handler != null)
            {
                handler(this, new ProcessorSteppedEventArgs(oldPC, newPC));
            }
        }

        private void OnEnterFrame(StackFrame oldFrame, StackFrame newFrame)
        {
            var handler = EnterFrame;
            if (handler != null)
            {
                handler(this, new StackFrameEventArgs(oldFrame, newFrame));
            }
        }

        private void OnExitFrame(StackFrame oldFrame, StackFrame newFrame)
        {
            var handler = ExitFrame;
            if (handler != null)
            {
                handler(this, new StackFrameEventArgs(oldFrame, newFrame));
            }
        }

        private void OnLocalVariableChanged(int index, Value oldValue, Value newValue)
        {
            var handler = LocalVariableChanged;
            if (handler != null)
            {
                handler(this, new LocalVariableChangedEventArgs(index, oldValue, newValue));
            }
        }

        private void OnQuit()
        {
            var handler = Quit;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        public event EventHandler<ProcessorSteppingEventArgs> Stepping;
        public event EventHandler<ProcessorSteppedEventArgs> Stepped;

        public event EventHandler<StackFrameEventArgs> EnterFrame;
        public event EventHandler<StackFrameEventArgs> ExitFrame;

        public event EventHandler<LocalVariableChangedEventArgs> LocalVariableChanged;

        public event EventHandler Quit;

        Value IExecutionContext.GetOperandValue(Operand operand)
        {
            return GetOperandValue(operand);
        }

        Value IExecutionContext.ReadByte(int address)
        {
            return Value.Number(story.Memory.ReadByte(address));
        }

        Value IExecutionContext.ReadVariable(Variable variable)
        {
            return ReadVariable(variable);
        }

        Value IExecutionContext.ReadVariableIndirectly(Variable variable)
        {
            return ReadVariable(variable, indirect: true);
        }

        Value IExecutionContext.ReadWord(int address)
        {
            return Value.Number(story.Memory.ReadWord(address));
        }

        void IExecutionContext.WriteByte(int address, byte value)
        {
            story.Memory.WriteByte(address, value);
        }

        void IExecutionContext.WriteProperty(int objNum, int propNum, ushort value)
        {
            WriteProperty(objNum, propNum, value);
        }

        void IExecutionContext.WriteVariable(Variable variable, Value value)
        {
            WriteVariable(variable, value);
        }

        void IExecutionContext.WriteVariableIndirectly(Variable variable, Value value)
        {
            WriteVariable(variable, value, indirect: true);
        }

        void IExecutionContext.WriteWord(int address, ushort value)
        {
            story.Memory.WriteWord(address, value);
        }

        void IExecutionContext.Call(int address, Operand[] operands = null, Variable storeVariable = null)
        {
            Call(address, operands, storeVariable);
        }

        int IExecutionContext.GetArgumentCount()
        {
            return CurrentFrame.Arguments.Count;
        }

        void IExecutionContext.Jump(short offset)
        {
            Jump(offset);
        }

        void IExecutionContext.Jump(Branch branch)
        {
            Jump(branch);
        }

        void IExecutionContext.Return(Value value)
        {
            Return(value);
        }

        int IExecutionContext.UnpackRoutineAddress(ushort byteAddress)
        {
            return story.UnpackRoutineAddress(byteAddress);
        }

        int IExecutionContext.UnpackStringAddress(ushort byteAddress)
        {
            return story.UnpackStringAddress(byteAddress);
        }

        int IExecutionContext.GetChild(int objNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            if (!obj.HasChild)
            {
                return 0;
            }
            else
            {
                return obj.Child.Number;
            }
        }

        int IExecutionContext.GetParent(int objNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            if (!obj.HasParent)
            {
                return 0;
            }
            else
            {
                return obj.Parent.Number;
            }
        }

        int IExecutionContext.GetSibling(int objNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            if (!obj.HasSibling)
            {
                return 0;
            }
            else
            {
                return obj.Sibling.Number;
            }
        }

        string IExecutionContext.GetShortName(int objNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            return obj.ShortName;
        }

        int IExecutionContext.GetNextProperty(int objNum, int propNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);

            int nextIndex = 0;
            if (propNum > 0)
            {
                var prop = obj.PropertyTable.GetByNumber(propNum);
                if (prop == null)
                {
                    throw new InvalidOperationException();
                }

                nextIndex = prop.Index + 1;
            }

            if (nextIndex == obj.PropertyTable.Count)
            {
                return 0;
            }

            return obj.PropertyTable[nextIndex].Number;
        }

        int IExecutionContext.GetPropertyData(int objNum, int propNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            var prop = obj.PropertyTable.GetByNumber(propNum);

            if (prop == null)
            {
                return story.ObjectTable.GetPropertyDefault(propNum);
            }

            if (prop.DataLength == 1)
            {
                return story.Memory.ReadByte(prop.DataAddress);
            }
            else if (prop.DataLength == 2)
            {
                return story.Memory.ReadWord(prop.DataAddress);
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        int IExecutionContext.GetPropertyDataAddress(int objNum, int propNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            var prop = obj.PropertyTable.GetByNumber(propNum);

            return prop != null
                ? prop.DataAddress
                : 0;
        }

        int IExecutionContext.GetPropertyDataLength(int dataAddress)
        {
            return story.Memory.ReadPropertyDataLength(dataAddress);
        }

        bool IExecutionContext.HasAttribute(int objNum, int attrNum)
        {
            return HasAttribute(objNum, attrNum);
        }

        void IExecutionContext.ClearAttribute(int objNum, int attrNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            obj.ClearAttribute(attrNum);
        }

        void IExecutionContext.SetAttribute(int objNum, int attrNum)
        {
            var obj = story.ObjectTable.GetByNumber(objNum);
            obj.SetAttribute(attrNum);
        }

        void IExecutionContext.RemoveFromParent(int objNum)
        {
            story.Memory.RemoveObjectFromParentByNumber(objNum);
        }

        void IExecutionContext.MoveTo(int objNum, int destNum)
        {
            story.Memory.MoveObjectToDestinationByNumber(objNum, destNum);
        }

        ushort[] IExecutionContext.ReadZWords(int address)
        {
            var reader = story.Memory.CreateReader(address);
            return reader.NextZWords();
        }

        string IExecutionContext.ParseZWords(IList<ushort> zwords)
        {
            return ZText.ZWordsAsString(zwords, ZTextFlags.All, story.Memory);
        }

        void IExecutionContext.Print(string text)
        {
            outputStreams.Print(text);
        }

        void IExecutionContext.Print(char ch)
        {
            outputStreams.Print(ch);
        }

        void IExecutionContext.Randomize(int seed)
        {
            random = new Random(seed);
        }

        int IExecutionContext.NextRandom(int range)
        {
            // range should be inclusive, so we need to subtract 1 since System.Range.Next makes it exclusive
            var minValue = 1;
            var maxValue = Math.Max(minValue, range - 1);
            return random.Next(minValue, maxValue);
        }

        void IExecutionContext.Quit()
        {
            OnQuit();
        }

        bool IExecutionContext.VerifyChecksum()
        {
            return story.ActualChecksum == story.Memory.ReadChecksum();
        }

        IScreen IExecutionContext.Screen
        {
            get { return screen; }
        }
    }
}
