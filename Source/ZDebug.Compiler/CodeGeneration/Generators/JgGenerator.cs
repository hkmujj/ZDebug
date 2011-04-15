﻿using ZDebug.Compiler.Generate;
using ZDebug.Core.Instructions;

namespace ZDebug.Compiler.CodeGeneration.Generators
{
    internal class JgGenerator : OpcodeGenerator
    {
        private readonly Operand op1;
        private readonly Operand op2;
        private readonly Branch branch;

        public JgGenerator(Operand op1, Operand op2, Branch branch)
            : base(OpcodeGeneratorKind.Jg)
        {
            this.op1 = op1;
            this.op2 = op2;
            this.branch = branch;
        }

        public override void Generate(ILBuilder il, ICompiler compiler)
        {
            // OPTIMIZE: Use IL evaluation stack if first op is SP and last instruction stored to SP.

            compiler.EmitLoadOperand(op1);
            il.Convert.ToInt16();

            compiler.EmitLoadOperand(op2);
            il.Convert.ToInt16();

            il.Compare.GreaterThan();
            compiler.EmitBranch(branch);
        }
    }
}
