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

namespace OraPkgMap
{
    internal class Args
    {
        public string Owner { get; set; }
        public string ObjectName { get; set; }
        public string PackageName { get; set; }
        public string Overload { get; set; }
        public string ArgumentName { get; set; }
        public decimal? Position { get; set; }
        public string DataType { get; set; }
        public bool Defaulted { get; set; }
        public string Direction { get; set; }
        public decimal? DataLength { get; set; }
        public decimal? DataPrecision { get; set; }
    }

    public class CodeGen
    {
        public string CreateClass(string connection, string owner, string package, string ns)
        {
            var all_args = new List<Args>();

            using (var conn = new OracleConnection(connection))
            {
                conn.Open();

                using (var cmd = new OracleCommand("select owner, object_name, package_name, overload, argument_name, position, data_type, defaulted, in_out, data_length, data_precision from ALL_ARGUMENTS where owner = :owner and package_name = :pkg order by position", conn))
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
                                DataType = dr[6] as string,
                                Defaulted = (dr[7] as string) == "Y",
                                Direction = dr[8] as string,
                                DataLength = dr[9] as decimal?,
                                DataPrecision = dr[10] as decimal?
                            };
                            all_args.Add(na);

                        }
                    }
                }

                conn.Close();
            }

            return CreateClass(ns, all_args);
        }

        private string CreateClass(string ns, List<Args> args)
        {
            var genUnit = new CodeCompileUnit();
            var genNamespace = new CodeNamespace(ns);
            genNamespace.Imports.Add(new CodeNamespaceImport("System"));
            genNamespace.Imports.Add(new CodeNamespaceImport("System.Data"));
            genNamespace.Imports.Add(new CodeNamespaceImport("Oracle.ManagedDataAccess.Client"));
            genNamespace.Imports.Add(new CodeNamespaceImport("Oracle.ManagedDataAccess.Types"));

            var genClass = new CodeTypeDeclaration(args.First().PackageName);
            genClass.IsClass = true;
            genClass.TypeAttributes = TypeAttributes.Public;
            genNamespace.Types.Add(genClass);
            genUnit.Namespaces.Add(genNamespace);

            foreach (var ag in args.GroupBy(a => $"{a.ObjectName}_{a.Overload}"))
            {
                var nm = new CodeMemberMethod();
                nm.Attributes = MemberAttributes.Public | MemberAttributes.Static;
                nm.Name = ag.First().ObjectName;
                var hasReturn = ag.Any(a => a.Direction == "OUT" && string.IsNullOrEmpty(a.ArgumentName));
                var returnArg = ag.FirstOrDefault(a => a.Direction == "OUT" && string.IsNullOrEmpty(a.ArgumentName));
                if (hasReturn)
                {
                    nm.ReturnType = OracleTypeToCodeRefType(returnArg.DataType);
                }
                else
                {
                    nm.ReturnType = new CodeTypeReference(typeof(void));
                }

                var con_in = new CodeParameterDeclarationExpression("OracleConnection", "con");
                nm.Parameters.Add(con_in);

                foreach (var p in ag.OrderBy(a => a.Defaulted).Where(a => !string.IsNullOrEmpty(a.ArgumentName)))
                {
                    var argref = new CodeParameterDeclarationExpression(OracleTypeToCodeRefType(p.DataType), $"{p.ArgumentName}{(p.Defaulted ? " = default" : string.Empty)}");
                    argref.Direction = StringToFieldDirection(p.Direction);
                    nm.Parameters.Add(argref);
                }

                var cxb = new StringBuilder();
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

                    cxb.Append($"\n\t{(p.Defaulted ? "\t" : "")}cmd.Parameters.Add(\"{p.ArgumentName}\", OracleDbType.{StringToDbType(p.DataType).ToString()}, {(p.Direction != "OUT" ? $"{p.ArgumentName}, " : "")}ParameterDirection.{StringToDirection(p.Direction).ToString()});");

                    if (p.Defaulted)
                    {
                        cxb.Append("\n\t}");
                    }
                }

                cxb.Append("\n\tcmd.ExecuteNonQuery();");

                foreach (var p in ag.OrderBy(a => a.Position).Where(a => (a.Direction == "OUT" || a.Direction == "IN_OUT") && !string.IsNullOrEmpty(a.ArgumentName)))
                {
                    cxb.Append($"\n\tif(!(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value).IsNull) {{\r\n\t\t{p.ArgumentName} = ({OracletypeToCastStringType(p.DataType)})(({StringToOracleType(p.DataType)})cmd.Parameters[\"{p.ArgumentName}\"].Value);\r\n\t}}\r\n\telse {{\r\n\t\t{p.ArgumentName} = null;\r\n\t}}");
                }

                if (hasReturn)
                {
                    cxb.Append($"\n\treturn cmd.Parameters[\"Return_Value\"].Value != DBNull.Value ? ({OracletypeToCastStringType(returnArg.DataType)})(({StringToOracleType(returnArg.DataType)})cmd.Parameters[\"Return_Value\"].Value) : default;");
                }
                cxb.Append("\n\t}");

                var cx = new CodeSnippetStatement(cxb.ToString());
                nm.Statements.Add(cx);
                genClass.Members.Add(nm);
            }

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
                    return "OracleString";
                case "DATE":
                    return "OracleDate";
                case "PL/SQL BOOLEAN":
                    return "OracleBoolean";
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
                    return OracleDbType.Varchar2;
                case "DATE":
                    return OracleDbType.Date;
                case "CHAR":
                    return OracleDbType.Char;
                case "LONG":
                    return OracleDbType.Long;
                case "PL/SQL BOOLEAN":
                    return OracleDbType.Boolean;
            }
            return OracleDbType.Raw;
        }

        private CodeTypeReference OracleTypeToCodeRefType(string dbt)
        {
            switch (dbt)
            {
                case "ROWID":
                case "NUMBER":
                    return new CodeTypeReference("decimal?");
                case "VARCHAR2":
                    return new CodeTypeReference(typeof(string));
                case "DATE":
                    return new CodeTypeReference("DateTime?");
                case "CHAR":
                    return new CodeTypeReference(typeof(char));
                case "LONG":
                    return new CodeTypeReference("long?");
                case "PL/SQL BOOLEAN":
                    return new CodeTypeReference("bool?");
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
                    return "string";
                case "DATE":
                    return "DateTime?";
                case "CHAR":
                    return "char";
                case "LONG":
                    return "long?";
                case "PL/SQL BOOLEAN":
                    return "bool?";
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
