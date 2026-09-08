using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(args[0]);
var pad = asm.GetType("Org.BouncyCastle.Bcpg.OpenPgp.PgpPad");
foreach (var m in pad.GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
  Console.WriteLine(m);
