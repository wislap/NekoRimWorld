using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Security.Cryptography;
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();
var wanted = new HashSet<string>(new[]{"ThingCategory","FoodTypeFlags","DrugCategory","QualityCategory","RotStage","StoragePriority","Gender","BackstorySlot","Passion","TargetIndex"});
var domains = new SortedDictionary<string,object>();
string TypeName(EntityHandle handle) {
 if(handle.Kind==HandleKind.TypeReference) {var t=reader.GetTypeReference((TypeReferenceHandle)handle);return reader.GetString(t.Namespace)+"."+reader.GetString(t.Name);}
 if(handle.Kind==HandleKind.TypeDefinition) {var t=reader.GetTypeDefinition((TypeDefinitionHandle)handle);return reader.GetString(t.Namespace)+"."+reader.GetString(t.Name);}
 return "";
}
foreach(var handle in reader.TypeDefinitions) {
 var type=reader.GetTypeDefinition(handle);var name=reader.GetString(type.Name);
 if(!wanted.Contains(name) || TypeName(type.BaseType)!="System.Enum")continue;
 var values=new Dictionary<string,string>();
 foreach(var fieldHandle in type.GetFields()) {
  var field=reader.GetFieldDefinition(fieldHandle);var constantHandle=field.GetDefaultValue();if(constantHandle.IsNil)continue;
  var constant=reader.GetConstant(constantHandle);var blob=reader.GetBlobReader(constant.Value);
  string value=constant.TypeCode switch {
   ConstantTypeCode.SByte=>blob.ReadSByte().ToString(),ConstantTypeCode.Byte=>blob.ReadByte().ToString(),
   ConstantTypeCode.Int16=>blob.ReadInt16().ToString(),ConstantTypeCode.UInt16=>blob.ReadUInt16().ToString(),
   ConstantTypeCode.Int32=>blob.ReadInt32().ToString(),ConstantTypeCode.UInt32=>blob.ReadUInt32().ToString(),
   ConstantTypeCode.Int64=>blob.ReadInt64().ToString(),ConstantTypeCode.UInt64=>blob.ReadUInt64().ToString(),_=>throw new Exception("Unexpected enum constant")};
  values[reader.GetString(field.Name)]=value;
 }
 bool flags=type.GetCustomAttributes().Any(h=>{var attr=reader.GetCustomAttribute(h);return attr.Constructor.Kind==HandleKind.MemberReference && TypeName(reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent)=="System.FlagsAttribute";});
 domains[TypeName(handle)]=new{flags,values};
}
if(domains.Count!=wanted.Count)throw new Exception("Expected exactly all declared enum domains");
File.WriteAllText(args[1],JsonSerializer.Serialize(new{source_assembly_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant(),domains},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(string.Join("\n",domains.Keys));
