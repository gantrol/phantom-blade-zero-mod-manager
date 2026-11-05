#include "MapSchemaCommandlet.h"
#include "Modules/ModuleManager.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonSerializer.h"
#include "Misc/FileHelper.h"
#include "Misc/EngineVersion.h"

IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, MapDataTools, "MapDataTools");

namespace
{
TSharedPtr<FJsonObject> PropertyType(FProperty* Property)
{
    auto Out = MakeShared<FJsonObject>();
    Out->SetStringField(TEXT("type"), Property->GetClass()->GetName());
    if (auto P = CastField<FStructProperty>(Property)) Out->SetStringField(TEXT("struct"), P->Struct->GetName());
    if (auto P = CastField<FEnumProperty>(Property))
    {
        Out->SetStringField(TEXT("enum"), P->GetEnum()->GetName());
        Out->SetObjectField(TEXT("inner"), PropertyType(P->GetUnderlyingProperty()));
    }
    if (auto P = CastField<FByteProperty>(Property))
        if (P->Enum) Out->SetStringField(TEXT("enum"), P->Enum->GetName());
    if (auto P = CastField<FArrayProperty>(Property)) Out->SetObjectField(TEXT("inner"), PropertyType(P->Inner));
    if (auto P = CastField<FSetProperty>(Property)) Out->SetObjectField(TEXT("inner"), PropertyType(P->ElementProp));
    if (auto P = CastField<FMapProperty>(Property))
    {
        Out->SetObjectField(TEXT("inner"), PropertyType(P->KeyProp));
        Out->SetObjectField(TEXT("value"), PropertyType(P->ValueProp));
    }
    return Out;
}
}

UMapSchemaCommandlet::UMapSchemaCommandlet()
{
    IsClient = false; IsServer = false; IsEditor = true; LogToConsole = true;
}

int32 UMapSchemaCommandlet::Main(const FString& Params)
{
    FString Output;
    if (!FParse::Value(*Params, TEXT("Output="), Output)) return 1;
    TArray<TSharedPtr<FJsonValue>> Types;
    for (TObjectIterator<UStruct> It; It; ++It)
    {
        if (It->IsA<UFunction>() || !It->GetPathName().StartsWith(TEXT("/Script/"))) continue;
        auto Type = MakeShared<FJsonObject>();
        Type->SetStringField(TEXT("name"), It->GetName());
        Type->SetStringField(TEXT("path"), It->GetPathName());
        TArray<TSharedPtr<FJsonValue>> Properties;
        int32 Index = 0;
        // Same ordering and editor-only exclusion as FUnversionedStructSchema.
        // Retain transient fields: their slots still belong to the schema.
        for (FProperty* P = It->PropertyLink; P; P = P->PropertyLinkNext)
        {
            if (P->IsEditorOnlyProperty()) continue;
            for (int32 Element = 0; Element < P->ArrayDim; ++Element)
            {
                auto Field = PropertyType(P);
                Field->SetStringField(TEXT("name"), P->GetName());
                Field->SetNumberField(TEXT("index"), Index++);
                Field->SetNumberField(TEXT("arrayIndex"), Element);
                Properties.Add(MakeShared<FJsonValueObject>(Field));
            }
        }
        Type->SetArrayField(TEXT("properties"), Properties);
        Types.Add(MakeShared<FJsonValueObject>(Type));
    }
    auto Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("engine"), FEngineVersion::Current().ToString());
    Root->SetStringField(TEXT("scope"), TEXT("editor-native-types-not-game-mappings"));
    Root->SetArrayField(TEXT("types"), Types);
    FString Text;
    FJsonSerializer::Serialize(Root, TJsonWriterFactory<>::Create(&Text));
    return FFileHelper::SaveStringToFile(Text, *Output, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM) ? 0 : 2;
}
