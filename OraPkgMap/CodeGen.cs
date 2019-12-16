using Oracle.ManagedDataAccess.Client;
using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using Oracle.ManagedDataAccess.Types;

namespace OraPkgMap
{

    internal class Args
    {
        public string Owner { get; set; }
        public string ObjectName { get; set; }
        public string PackageName { get; set; }
        public string Overload { get; set; }
        public string ArgumentName { get; set; }
        public decimal? DataLevel { get; set; }
        public decimal? Position { get; set; }
        public string DataType { get; set; }
        public bool Defaulted { get; set; }
        public string Direction { get; set; }
        public decimal? DataLength { get; set; }
        public decimal? DataPrecision { get; set; }
    }

    public class CodeGen
    {
        private TextInfo TextInfo { get; } = new CultureInfo("en-US", false).TextInfo;

        public string CreateClass(string connection, string owner, string package, string ns, bool sync, bool async)
        {
            var all_args = new List<Args>();

            using (var conn = new OracleConnection(connection))
            {
                conn.Open();

                using (var cmd = new OracleCommand("select owner, object_name, package_name, overload, argument_name, position, data_level, data_type, defaulted, in_out, data_length, data_precision from ALL_ARGUMENTS where owner = :owner and package_name = :pkg order by position", conn))
                {
                    cmd.Parameters.Add(":owner", owner);
                    cmd.Parameters.Add(":pkg", package);

                    using (var dr = cmd.ExecuteReader())
                    {
                        while (dr.Read())
                        {
                            var na = new Args()
                            {
                                Owner = dr[0] as string,
                                ObjectName = dr[1] as string,
                                PackageName = dr[2] as string,
                                Overload = dr[3] as string,
                                ArgumentName = dr[4] as string,
                                Position = dr[5] as decimal?,
                                DataLevel = dr[6] as decimal?,
                                DataType = dr[7] as string,
                                Defaulted = (dr[8] as string) == "Y",
                                Direction = dr[9] as string,
                                DataLength = dr[10] as decimal?,
                                DataPrecision = dr[11] as decimal?
                            };
                            all_args.Add(na);

                        }
                    }
                }

                conn.Close();
            }

            return CreateClass(ns, all_args, sync, async);
        }

        private string CreateClass(string ns, List<Args> args, bool sync, bool async)
        {
            var genUnit = new CodeCompileUnit();
            var genNamespace = new CodeNamespace(ns);
            genNamespace.Imports.Add(new CodeNamespaceImport("System"));
            genNamespace.Imports.Add(new CodeNamespaceImport("System.Data"));
            genNamespace.Imports.Add(new CodeNamespaceImport("Oracle.ManagedDataAccess.Client"));
            genNamespace.Imports.Add(new CodeNamespaceImport("Oracle.ManagedDataAccess.Types"));
            if (async)
            {
                genNamespace.Imports.Add(new CodeNamespaceImport("System.Threading.Tasks"));
            }

            var genClass = new CodeTypeDeclaration(args.First().PackageName);
            genClass.IsClass = true;
            genClass.TypeAttributes = TypeAttributes.Public;
            genNamespace.Types.Add(genClass);


            foreach (var ag in args.GroupBy(a => $"{a.ObjectName}_{a.Overload}"))
            {
                if (sync)
                {
                    genClass.Members.Add(MakeMethod(ag.ToList(), sync && async));
                }
                if (async)
                {
                    var needsOutClass = ag.Where(a => a.Direction == "OUT");
                    if (needsOutClass.Count() > 1)
                    {
                        genNamespace.Types.Add(MakeOutClass(needsOutClass));
                    }
                    genClass.Members.Add(MakeMethod(ag.ToList(), sync && async, async));
                }
            }

            genUnit.Namespaces.Add(genNamespace);
            
            var cp = CodeDomProvider.CreateProvider("CSharp");
            using (var ms = new MemoryStream())
            {
                using (var sw = new StreamWriter(ms))
                {
                    cp.GenerateCodeFromCompileUnit(genUnit, sw, new CodeGeneratorOptions()
                    {
                        BracingStyle = "C"
                    });
                    sw.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private CodeTypeDeclaration MakeOutClass(IEnumerable<Args> args)
        {
            var genClass = new CodeTypeDeclaration($"{args.First().ObjectName}OUT");
            genClass.IsClass = true;
            genClass.TypeAttributes = TypeAttributes.Public;

            foreach (var arg in args)
            {
                var nm = new CodeMemberField();
                nm.Attributes = MemberAttributes.Public | MemberAttributes.Final;
                nm.Name = arg.ArgumentName ?? "RETURN";
                nm.Type = OracleTypeToCodeRefType(arg.DataType, false);

                genClass.Members.Add(nm);
            }

            return genClass;
        }


        private CodeMemberMethod MakeMethod(List<Args> ag, bool hasBoth, bool async = false)
        {
            var nm = new CodeMemberMethod();
            nm.Attributes = MemberAttributes.Public | MemberAttributes.Static;

            if (hasBoth && async)
            {
                nm.Name = $"{ag.First().ObjectName}ASYNC";
            }
            else
            {
                nm.Name = ag.First().ObjectName;
            }
            var outArgs = ag.Where(a => a.Direction == "OUT");
            var hasReturn = ag.Any(a => a.Direction == "OUT" && string.IsNullOrEmpty(a.ArgumentName));
            var outAsReturn = outArgs.Count() == 1 && !hasReturn;
            var returnArg = ag.FirstOrDefault(a => a.Direction == "OUT" && string.IsNullOrEmpty(a.ArgumentName)) ?? ag.FirstOrDefault(a => a.Direction == "OUT");
            var canGenForManaged = ag.All(a => a.DataLevel == 0);
            var allArgs = ag.OrderBy(a => a.Defaulted).Where(a => !string.IsNullOrEmpty(a.ArgumentName) && a.DataLevel == 0);

            if (hasReturn || outAsReturn)
            {
                if (outArgs.Count() > 1 && async)
                {
                    nm.ReturnType = new CodeTypeReference($"async Task<{ag.First().ObjectName}OUT>");
                }
                else
                {
                    nm.ReturnType = OracleTypeToCodeRefType(returnArg.DataType, async);
                }
            }
            else
            {
                if (async)
                {
                    if (outArgs.Count() > 1)
                    {
                        nm.ReturnType = new CodeTypeReference($"async Task<{ag.First().ObjectName}OUT>");
                    }
                    else
                    {
                        nm.ReturnType = new CodeTypeReference("async Task");
                    }
                }
                else
                {
                    nm.ReturnType = new CodeTypeReference(typeof(void));
                }
            }

            var con_in = new CodeParameterDeclarationExpression("OracleConnection", "con");
            nm.Parameters.Add(con_in);

            foreach (var p in allArgs)
            {
                if ((!async && ((outAsReturn && p.Direction == "IN") || !outAsReturn)) || (async && p.Direction == "IN"))
                {
                    var argref = new CodeParameterDeclarationExpression(OracleTypeToCodeRefType(p.DataType, false), $"{p.ArgumentName}{(p.Defaulted ? " = default" : string.Empty)}");
                    argref.Direction = StringToFieldDirection(p.Direction);
                    nm.Parameters.Add(argref);
                }
            }

            var cxb = new StringBuilder();
            if (canGenForManaged)
            {
                if (async && outArgs.Count() > 1)
                {
                    cxb.Append($"var ret = new {ag.First().ObjectName}OUT();\n");
                }
                cxb.Append($"using(var cmd = new OracleCommand(\"{ag.First().Owner}.{ag.First().PackageName}.{ag.First().ObjectName}\", con)) {{");
                cxb.Append("\n\tcmd.CommandType = CommandType.StoredProcedure;\n\tcmd.BindByName = true;");
                if (hasReturn)
                {
                    cxb.Append($"\n\tcmd.Parameters.Add(\"Return_Value\", OracleDbType.{StringToDbType(returnArg.DataType).ToString()}, ParameterDirection.ReturnValue);");
                }
                foreach (var p in ag.OrderBy(a => a.Position).Where(a => !string.IsNullOrEmpty(a.ArgumentName)))
                {
                    if (p.Defaulted)
                    {
                        cxb.Append($"\n\tif({p.ArgumentName} != default) {{");
                    }

                    cxb.Append($"\n\t{(p.Defaulted ? "\t" : "")}cmd.Parameters.Add(new OracleParameter(\"{p.ArgumentName}\", OracleDbType.{StringToDbType(p.DataType).ToString()}, {(p.Direction != "OUT" ? $"{p.ArgumentName}, " : "")}ParameterDirection.{StringToDirection(p.Direction).ToString()}){(p.DataLength.HasValue ? $" {{ Size = {p.DataLength} }}" : string.Empty)});");

                    if (p.Defaulted)
                    {
                        cxb.Append("\n\t}");
                    }
                }

                if (async)
                {
                    cxb.Append("\n\tawait cmd.ExecuteNonQueryAsync();");
                }
                else
                {
                    cxb.Append("\n\tcmd.ExecuteNonQuery();");
                }

                foreach (var p in ag.OrderBy(a => a.Position).Where(a => (a.Direction == "OUT" || a.Direction == "IN_OUT") && !string.IsNullOrEmpty(a.ArgumentName)))
                {
                    if (async && outArgs.Count() > 1)
                    {
                        cxb.Append($"\n\tif(!(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value).IsNull) {{\r\n\t\tret.{p.ArgumentName} = ({OracletypeToCastStringType(p.DataType)})(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value);\r\n\t}}\r\n\telse {{\r\n\t\tret.{p.ArgumentName} = null;\r\n\t}}");
                    }
                    else
                    {
                        if (outAsReturn)
                        {
                            cxb.Append($"\n\tif(!(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value).IsNull) {{\r\n\t\treturn ({OracletypeToCastStringType(p.DataType)})(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value);\r\n\t}}\r\n\telse {{\r\n\t\treturn default;\r\n\t}}");
                        }
                        else
                        {
                            cxb.Append($"\n\tif(!(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value).IsNull) {{\r\n\t\t{p.ArgumentName} = ({OracletypeToCastStringType(p.DataType)})(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value);\r\n\t}}\r\n\telse {{\r\n\t\t{p.ArgumentName} = null;\r\n\t}}");
                        }
                    }
                }

                if (hasReturn)
                {
                    if (async && outArgs.Count() > 1)
                    {
                        cxb.Append($"\n\tret.RETURN = cmd.Parameters[\"Return_Value\"].Value != DBNull.Value ? ({OracletypeToCastStringType(returnArg.DataType)})(({StringToOracleType(returnArg.DataType)})cmd.Parameters[\"Return_Value\"].Value) : default;");
                    }
                    else
                    {
                        cxb.Append($"\n\treturn cmd.Parameters[\"Return_Value\"].Value != DBNull.Value ? ({OracletypeToCastStringType(returnArg.DataType)})(({StringToOracleType(returnArg.DataType)})cmd.Parameters[\"Return_Value\"].Value) : default;");
                    }
                }
                cxb.Append("\n\t}");
                if (async && outArgs.Count() > 1)
                {
                    cxb.Append("\n\treturn ret;");
                }
            }
            else
            {
                cxb.Append($"\t/// UDT classes are not supported in the managed driver\n\tthrow new NotImplementedException();");
            }

            var cx = new CodeSnippetStatement(cxb.ToString());
            nm.Statements.Add(cx);

            return nm;
        }

        private string StringToOracleType(string x)
        {
            switch (x)
            {
                case "ROWID":
                case "NUMBER":
                case "LONG":
                    return "OracleDecimal";
                case "VARCHAR2":
                case "CHAR":
                case "CLOB":
                    return "OracleString";
                case "DATE":
                    return "OracleDate";
                case "PL/SQL BOOLEAN":
                    return "OracleBoolean";
                case "REF CURSOR":
                    return "OracleRefCursor";
            }
            return "object";
        }

        private OracleDbType StringToDbType(string x)
        {
            switch (x)
            {
                case "ROWID":
                case "NUMBER":
                    return OracleDbType.Decimal;
                case "VARCHAR2":
                case "CLOB":
                    return OracleDbType.Varchar2;
                case "DATE":
                    return OracleDbType.Date;
                case "CHAR":
                    return OracleDbType.Char;
                case "LONG":
                    return OracleDbType.Long;
                case "PL/SQL BOOLEAN":
                    return OracleDbType.Boolean;
                case "PL/SQL RECORD":
                    return OracleDbType.Raw;
                case "REF CURSOR":
                    return OracleDbType.RefCursor;
                default:
                    return (OracleDbType)Enum.Parse(typeof(OracleDbType), x);
            }
        }

        private CodeTypeReference OracleTypeToCodeRefType(string dbt, bool async)
        {
            switch (dbt)
            {
                case "ROWID":
                case "NUMBER":
                    return new CodeTypeReference(async ? "async Task<decimal?>" : "decimal?");
                case "VARCHAR2":
                case "CLOB":
                    return async ? new CodeTypeReference("async Task<string>") : new CodeTypeReference(typeof(string));
                case "DATE":
                    return new CodeTypeReference(async ? "async Task<DateTime?>" : "DateTime?");
                case "CHAR":
                    return async ? new CodeTypeReference("async Task<char>") : new CodeTypeReference(typeof(char));
                case "LONG":
                    return new CodeTypeReference(async ? "async Task<long?>" : "long?");
                case "PL/SQL BOOLEAN":
                    return new CodeTypeReference(async ? "async Task<bool?>" : "bool?");
                case "PL/SQL RECORD":
                    return async ? new CodeTypeReference("async Task<object>") : new CodeTypeReference(typeof(object));
                case "REF CURSOR":
                    return async ? new CodeTypeReference("async Task<OracleRefCursor>") : new CodeTypeReference(typeof(OracleRefCursor));
            }
            return new CodeTypeReference("??");
        }

        private string OracletypeToCastStringType(string x)
        {
            switch (x)
            {
                case "ROWID":
                case "NUMBER":
                    return "decimal?";
                case "VARCHAR2":
                case "CLOB":
                    return "string";
                case "DATE":
                    return "DateTime?";
                case "CHAR":
                    return "char";
                case "LONG":
                    return "long?";
                case "PL/SQL BOOLEAN":
                    return "bool?";
                case "REF CURSOR":
                    return "OracleRefCursor";
            }
            return "object";
        }

        private FieldDirection StringToFieldDirection(string x)
        {
            switch (x)
            {
                case "IN":
                    return FieldDirection.In;
                case "OUT":
                    return FieldDirection.Out;
                case "IN_OUT":
                    return FieldDirection.Ref;
            }
            return FieldDirection.In;
        }

        private ParameterDirection StringToDirection(string x)
        {
            switch (x)
            {
                case "IN":
                    return ParameterDirection.Input;
                case "OUT":
                    return ParameterDirection.Output;
                case "IN_OUT":
                    return ParameterDirection.InputOutput;
            }
            return ParameterDirection.Input;
        }
    }
}
