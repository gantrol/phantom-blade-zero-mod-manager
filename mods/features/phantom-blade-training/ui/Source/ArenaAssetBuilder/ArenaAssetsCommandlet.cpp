#include "ArenaAssetsCommandlet.h"
#include "PBZWidgetCookTypes.h"
#include "BasicAttributeCookType.h"
#include "GameplayEffect.h"
#include "Modules/ModuleManager.h"
#include "WidgetBlueprint.h"
#include "Blueprint/WidgetTree.h"
#include "Blueprint/WidgetBlueprintGeneratedClass.h"
#include "Components/Border.h"
#include "Components/Button.h"
#include "Components/ButtonSlot.h"
#include "Components/CanvasPanel.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/HorizontalBox.h"
#include "Components/HorizontalBoxSlot.h"
#include "Components/Image.h"
#include "Components/Overlay.h"
#include "Components/OverlaySlot.h"
#include "Components/ScaleBox.h"
#include "Components/ScaleBoxSlot.h"
#include "Components/ScrollBox.h"
#include "Components/ScrollBoxSlot.h"
#include "Components/SizeBox.h"
#include "Components/SizeBoxSlot.h"
#include "Components/Spacer.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"
#include "Components/VerticalBoxSlot.h"
#include "Components/WidgetSwitcher.h"
#include "EdGraphSchema_K2.h"
#include "K2Node_ComponentBoundEvent.h"
#include "K2Node_Event.h"
#include "K2Node_CallFunction.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Kismet/KismetSystemLibrary.h"
#include "KismetCompiler.h"
#include "Misc/PackageName.h"
#include "UObject/SavePackage.h"

IMPLEMENT_MODULE(FDefaultModuleImpl, ArenaAssetBuilder)

namespace
{
class FDesigner
{
public:
    UWidgetBlueprint* Blueprint;
    UWidgetTree* Tree;
    TArray<TPair<FName, FName>> ClickEvents;

    explicit FDesigner(UPackage* Package)
    {
        Package->FullyLoad();
        Blueprint = FindObject<UWidgetBlueprint>(Package, TEXT("W_Arena"));
        if (Blueprint)
        {
            const auto Graphs = Blueprint->UbergraphPages;
            for (const auto& Graph : Graphs) FBlueprintEditorUtils::RemoveGraph(Blueprint, Graph.Get());
            Blueprint->WidgetTree = NewObject<UWidgetTree>(Blueprint, NAME_None, RF_Transactional);
        }
        else
        {
            Blueprint = CastChecked<UWidgetBlueprint>(FKismetEditorUtilities::CreateBlueprint(
                UPBZSystemWidgetBase::StaticClass(), Package, TEXT("W_Arena"), BPTYPE_Normal,
                UWidgetBlueprint::StaticClass(), UWidgetBlueprintGeneratedClass::StaticClass()));
        }
        Tree = Blueprint->WidgetTree;
        Blueprint->WidgetVariableNameToGuidMap.Reset();
    }

    template<class T> T* Widget(const FString& Name)
    {
        auto* Result = Tree->ConstructWidget<T>(T::StaticClass(), FName(Name));
        Result->bIsVariable = true;
        Blueprint->OnVariableAdded(Result->GetFName());
        return Result;
    }

    UTextBlock* Text(const FString& Name, int32 Size = 22)
    {
        auto* T = Widget<UTextBlock>(Name);
        auto Font = T->GetFont();
        Font.Size = Size;
        T->SetFont(Font);
        T->SetColorAndOpacity(FSlateColor(FLinearColor(0.82f, 0.80f, 0.74f, 1.f)));
        T->SetAutoWrapText(true);
        return T;
    }

    UButton* Button(const FString& Name, const FString& Action, bool Primary = false)
    {
        auto* B = Widget<UButton>(Name);
        FButtonStyle Style = B->GetStyle();
        const auto Brush = [](FLinearColor Color) {
            FSlateBrush R; R.DrawAs = ESlateBrushDrawType::Image;
            R.TintColor = FSlateColor(Color); return R;
        };
        Style.Normal = Brush(Primary ? FLinearColor(.24f, .033f, .021f, .95f) : FLinearColor::Transparent);
        Style.Hovered = Brush(Primary ? FLinearColor(.34f, .055f, .035f, 1) : FLinearColor(.7f, .65f, .55f, .12f));
        Style.Pressed = Brush(Primary ? FLinearColor(.17f, .022f, .015f, 1) : FLinearColor(.7f, .65f, .55f, .2f));
        Style.NormalPadding = FMargin(20, 12);
        Style.PressedPadding = Style.NormalPadding;
        B->SetStyle(Style);
        auto* Label = Text(Name + TEXT("Label"));
        Label->SetJustification(ETextJustify::Center);
        B->AddChild(Label);
        ClickEvents.Emplace(B->GetFName(), FName(TEXT("Arena_") + Action));
        return B;
    }

    void Row(UVerticalBox* Parent, UWidget* Child, float Bottom = 16)
    {
        auto* Slot = Parent->AddChildToVerticalBox(Child);
        Slot->SetPadding(FMargin(0, 0, 0, Bottom));
    }

    void Fill(UHorizontalBox* Parent, UWidget* Child, float Weight, float Right = 0)
    {
        auto* Slot = Parent->AddChildToHorizontalBox(Child);
        FSlateChildSize Size; Size.SizeRule = ESlateSizeRule::Fill; Size.Value = Weight;
        Slot->SetSize(Size);
        Slot->SetPadding(FMargin(0, 0, Right, 0));
    }

    void Auto(UHorizontalBox* Parent, UWidget* Child, float Right = 0)
    {
        auto* Slot = Parent->AddChildToHorizontalBox(Child);
        Slot->SetVerticalAlignment(VAlign_Center);
        Slot->SetPadding(FMargin(0, 0, Right, 0));
    }

    USizeBox* Box(const FString& Name, UWidget* Child, float Width, float Height)
    {
        auto* Result = Widget<USizeBox>(Name);
        if (Width > 0) Result->SetWidthOverride(Width);
        if (Height > 0) Result->SetHeightOverride(Height);
        Result->AddChild(Child);
        return Result;
    }

    UBorder* Color(const FString& Name, FLinearColor Tint)
    {
        auto* Result = Widget<UBorder>(Name);
        Result->SetBrushColor(Tint);
        Result->SetPadding(FMargin(0));
        Result->SetVisibility(ESlateVisibility::HitTestInvisible);
        return Result;
    }

    USizeBox* Rule(const FString& Name)
    {
        return Box(Name + TEXT("Frame"), Color(Name, FLinearColor(.55f, .51f, .44f, .35f)), 0, 1);
    }

    UScaleBox* Image(const FString& Name, EStretch::Type Stretch = EStretch::ScaleToFit)
    {
        auto* Scale = Widget<UScaleBox>(Name + TEXT("Scale"));
        Scale->SetStretch(Stretch);
        Scale->SetClipping(EWidgetClipping::ClipToBounds);
        Scale->AddChild(Widget<UImage>(Name));
        return Scale;
    }

    UButton* Choice(const FString& Name, float Height)
    {
        auto* B = Button(Name, Name);
        auto* Label = CastChecked<UTextBlock>(B->GetChildAt(0));
        B->RemoveChild(Label);
        Label->SetJustification(ETextJustify::Left);
        Label->SetAutoWrapText(false);
        Label->SetColorAndOpacity(FSlateColor(FLinearColor(0.84f, 0.81f, 0.75f, 1.f)));
        auto Font = Label->GetFont();
        Font.Size = 18;
        Label->SetFont(Font);
        auto* SelectedLabel = Text(Name + TEXT("SelectedLabel"), 18);
        SelectedLabel->SetJustification(ETextJustify::Left);
        SelectedLabel->SetAutoWrapText(false);
        SelectedLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.08f, 0.07f, 0.06f, 1.f)));
        SelectedLabel->SetVisibility(ESlateVisibility::Collapsed);
        auto* SubLabel = Text(Name + TEXT("SubLabel"), 14);
        SubLabel->SetJustification(ETextJustify::Right);
        SubLabel->SetAutoWrapText(false);
        SubLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.58f, 0.54f, 0.49f, 1.f)));
        auto* SelectedSubLabel = Text(Name + TEXT("SelectedSubLabel"), 14);
        SelectedSubLabel->SetJustification(ETextJustify::Right);
        SelectedSubLabel->SetAutoWrapText(false);
        SelectedSubLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.22f, 0.19f, 0.16f, 1.f)));
        SelectedSubLabel->SetVisibility(ESlateVisibility::Collapsed);
        auto Style = B->GetStyle();
        Style.NormalPadding = Style.PressedPadding = FMargin(0);
        B->SetStyle(Style);
        auto* Layers = Widget<UOverlay>(Name + TEXT("Layers"));
        auto* RowSize = Box(Name + TEXT("RowSize"), Layers, 0, Height);
        if (auto* SizeSlot = Cast<USizeBoxSlot>(Layers->Slot))
        {
            SizeSlot->SetHorizontalAlignment(HAlign_Fill);
            SizeSlot->SetVerticalAlignment(VAlign_Fill);
        }
        B->AddChild(RowSize);
        if (auto* BtnSlot = Cast<UButtonSlot>(RowSize->Slot))
        {
            BtnSlot->SetPadding(FMargin(0));
            BtnSlot->SetHorizontalAlignment(HAlign_Fill);
            BtnSlot->SetVerticalAlignment(VAlign_Fill);
        }
        auto* Tint = Color(Name + TEXT("Selected"), FLinearColor(0.85f, 0.81f, 0.73f, 0.96f));
        auto* TintSlot = Layers->AddChildToOverlay(Tint);
        TintSlot->SetHorizontalAlignment(HAlign_Fill);
        TintSlot->SetVerticalAlignment(VAlign_Fill);
        auto* RowBox = Widget<UHorizontalBox>(Name + TEXT("Row"));
        auto* RowOverlaySlot = Layers->AddChildToOverlay(RowBox);
        RowOverlaySlot->SetHorizontalAlignment(HAlign_Fill);
        RowOverlaySlot->SetVerticalAlignment(VAlign_Fill);
        RowOverlaySlot->SetPadding(FMargin(14, 0, 14, 0));
        auto* ImgBox = Box(Name + TEXT("ImageFrame"), Image(Name + TEXT("Image")), Height - 12, Height - 12);
        Auto(RowBox, ImgBox, 16);
        CastChecked<UHorizontalBoxSlot>(ImgBox->Slot)->SetHorizontalAlignment(HAlign_Left);
        auto* LabelOverlay = Widget<UOverlay>(Name + TEXT("LabelOverlay"));
        auto* UnselSlot = LabelOverlay->AddChildToOverlay(Label);
        UnselSlot->SetHorizontalAlignment(HAlign_Fill);
        UnselSlot->SetVerticalAlignment(VAlign_Center);
        auto* SelSlot = LabelOverlay->AddChildToOverlay(SelectedLabel);
        SelSlot->SetHorizontalAlignment(HAlign_Fill);
        SelSlot->SetVerticalAlignment(VAlign_Center);
        Fill(RowBox, LabelOverlay, 1, 10);
        CastChecked<UHorizontalBoxSlot>(LabelOverlay->Slot)->SetHorizontalAlignment(HAlign_Fill);
        CastChecked<UHorizontalBoxSlot>(LabelOverlay->Slot)->SetVerticalAlignment(VAlign_Center);
        auto* SubOverlay = Widget<UOverlay>(Name + TEXT("SubLabelOverlay"));
        auto* UnselSubSlot = SubOverlay->AddChildToOverlay(SubLabel);
        UnselSubSlot->SetHorizontalAlignment(HAlign_Right);
        UnselSubSlot->SetVerticalAlignment(VAlign_Center);
        auto* SelSubSlot = SubOverlay->AddChildToOverlay(SelectedSubLabel);
        SelSubSlot->SetHorizontalAlignment(HAlign_Right);
        SelSubSlot->SetVerticalAlignment(VAlign_Center);
        Auto(RowBox, SubOverlay, 0);
        CastChecked<UHorizontalBoxSlot>(SubOverlay->Slot)->SetHorizontalAlignment(HAlign_Right);
        return B;
    }

    UButton* ChoiceChip(const FString& Name, float Width, float Height)
    {
        auto* B = Button(Name, Name);
        auto* Label = CastChecked<UTextBlock>(B->GetChildAt(0));
        B->RemoveChild(Label);
        Label->SetJustification(ETextJustify::Center);
        Label->SetAutoWrapText(false);
        auto Font = Label->GetFont();
        Font.Size = 15;
        Label->SetFont(Font);
        Label->SetColorAndOpacity(FSlateColor(FLinearColor(0.84f, 0.81f, 0.75f, 1.f)));
        auto* SelectedLabel = Text(Name + TEXT("SelectedLabel"), 15);
        SelectedLabel->SetJustification(ETextJustify::Center);
        SelectedLabel->SetAutoWrapText(false);
        SelectedLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.08f, 0.07f, 0.06f, 1.f)));
        SelectedLabel->SetVisibility(ESlateVisibility::Collapsed);
        auto Style = B->GetStyle();
        Style.NormalPadding = Style.PressedPadding = FMargin(0);
        B->SetStyle(Style);
        auto* Layers = Widget<UOverlay>(Name + TEXT("Layers"));
        auto* ChipSize = Box(Name + TEXT("ChipSize"), Layers, Width, Height);
        if (auto* SizeSlot = Cast<USizeBoxSlot>(Layers->Slot))
        {
            SizeSlot->SetHorizontalAlignment(HAlign_Fill);
            SizeSlot->SetVerticalAlignment(VAlign_Fill);
        }
        B->AddChild(ChipSize);
        if (auto* BtnSlot = Cast<UButtonSlot>(ChipSize->Slot))
        {
            BtnSlot->SetPadding(FMargin(0));
            BtnSlot->SetHorizontalAlignment(HAlign_Fill);
            BtnSlot->SetVerticalAlignment(VAlign_Fill);
        }
        auto* BaseBg = Color(Name + TEXT("BaseBg"), FLinearColor(0.14f, 0.13f, 0.11f, 0.85f));
        auto* BaseSlot = Layers->AddChildToOverlay(BaseBg);
        BaseSlot->SetHorizontalAlignment(HAlign_Fill);
        BaseSlot->SetVerticalAlignment(VAlign_Fill);
        auto* Tint = Color(Name + TEXT("Selected"), FLinearColor(0.85f, 0.81f, 0.73f, 0.96f));
        auto* TintSlot = Layers->AddChildToOverlay(Tint);
        TintSlot->SetHorizontalAlignment(HAlign_Fill);
        TintSlot->SetVerticalAlignment(VAlign_Fill);
        auto* UnselSlot = Layers->AddChildToOverlay(Label);
        UnselSlot->SetHorizontalAlignment(HAlign_Center);
        UnselSlot->SetVerticalAlignment(VAlign_Center);
        auto* SelSlot = Layers->AddChildToOverlay(SelectedLabel);
        SelSlot->SetHorizontalAlignment(HAlign_Center);
        SelSlot->SetVerticalAlignment(VAlign_Center);
        return B;
    }

    USizeBox* StepButton(const FString& Name, const FString& Symbol)
    {
        auto* B = Button(Name, Name);
        auto Style = B->GetStyle();
        const auto Brush = [](FLinearColor C) {
            FSlateBrush R; R.DrawAs = ESlateBrushDrawType::Image;
            R.TintColor = FSlateColor(C); return R;
        };
        Style.Normal = Brush(FLinearColor(0.16f, 0.15f, 0.13f, 0.9f));
        Style.Hovered = Brush(FLinearColor(0.28f, 0.25f, 0.21f, 1.f));
        Style.Pressed = Brush(FLinearColor(0.11f, 0.10f, 0.09f, 1.f));
        Style.NormalPadding = Style.PressedPadding = FMargin(0);
        B->SetStyle(Style);
        auto* Label = CastChecked<UTextBlock>(B->GetChildAt(0));
        Label->SetText(FText::FromString(Symbol));
        Label->SetAutoWrapText(false);
        auto Font = Label->GetFont();
        Font.Size = 18;
        Label->SetFont(Font);
        if (auto* BtnSlot = Cast<UButtonSlot>(Label->Slot))
        {
            BtnSlot->SetPadding(FMargin(0));
            BtnSlot->SetHorizontalAlignment(HAlign_Center);
            BtnSlot->SetVerticalAlignment(VAlign_Center);
        }
        return Box(Name + TEXT("Size"), B, 34, 32);
    }

    void Build()
    {
        auto* Canvas = Widget<UCanvasPanel>(TEXT("Root"));
        Tree->RootWidget = Canvas;
        // A centered logical surface keeps the original navigation and safe margins.
        auto* Scale = Widget<UScaleBox>(TEXT("SafeScale"));
        Scale->SetStretch(EStretch::ScaleToFit);
        Scale->SetStretchDirection(EStretchDirection::DownOnly);
        auto* CanvasSlot = Canvas->AddChildToCanvas(Scale);
        CanvasSlot->SetAnchors(FAnchors(0, 0, 1, 1));
        CanvasSlot->SetOffsets(FMargin(96, 58, 96, 74));
        auto* Layout = Widget<UVerticalBox>(TEXT("Layout"));
        Scale->AddChild(Box(TEXT("ContentFrame"), Layout, 1580, 740));
        Row(Layout, Text(TEXT("CurrentStatus"), 20), 24);
        auto* Main = Widget<UHorizontalBox>(TEXT("Main"));
        auto* MainSlot = Layout->AddChildToVerticalBox(Main);
        FSlateChildSize Stretch; Stretch.SizeRule = ESlateSizeRule::Fill;
        MainSlot->SetSize(Stretch);
        auto* Left = Widget<UVerticalBox>(TEXT("OpponentColumn"));
        Auto(Main, Box(TEXT("OpponentWidth"), Left, 420, 0));
        CastChecked<UHorizontalBoxSlot>(Left->GetParent()->Slot)->SetVerticalAlignment(VAlign_Fill);
        Row(Left, Text(TEXT("OpponentHeading"), 26), 20);
        Row(Left, Rule(TEXT("OpponentRule")), 20);
        auto* BossScroll = Widget<UScrollBox>(TEXT("BossScroll"));
        BossScroll->SetScrollbarThickness(FVector2D(3, 3));
        BossScroll->SetScrollbarPadding(FMargin(8, 0));
        BossScroll->SetNavigationDestination(EDescendantScrollDestination::IntoView);
        BossScroll->SetScrollWhenFocusChanges(EScrollWhenFocusChanges::InstantScroll);
        auto* ScrollSlot = Left->AddChildToVerticalBox(BossScroll);
        ScrollSlot->SetSize(Stretch);
        for (int32 I = 0; I != 64; ++I)
        {
            auto* B = Choice(FString::Printf(TEXT("Boss%d"), I), 68);
            CastChecked<UScrollBoxSlot>(BossScroll->AddChild(B))->SetPadding(FMargin(0, 0, 0, 8));
        }
        Auto(Main, Box(TEXT("ColumnGap"), Widget<USpacer>(TEXT("ColumnSpacer")), 44, 0));
        auto* Right = Widget<UVerticalBox>(TEXT("SelectionColumn"));
        Fill(Main, Right, 1);
        Row(Right, Text(TEXT("PendingStatus"), 18), 8);
        Row(Right, Text(TEXT("OpponentName"), 36), 14);
        auto* Switcher = Widget<UWidgetSwitcher>(TEXT("SceneSwitcher"));
        auto* RegionSlot = Right->AddChildToVerticalBox(Switcher);
        RegionSlot->SetSize(Stretch);
        auto* Preview = Widget<UVerticalBox>(TEXT("Preview"));
        Switcher->AddChild(Preview);
        auto* Art = Widget<UOverlay>(TEXT("PreviewArt"));
        Art->SetClipping(EWidgetClipping::ClipToBounds);
        Art->AddChildToOverlay(Image(TEXT("SceneImage"), EStretch::ScaleToFill));
        auto* PortraitSlot = Art->AddChildToOverlay(Box(TEXT("PortraitFrame"), Image(TEXT("Portrait")), 230, 230));
        PortraitSlot->SetHorizontalAlignment(HAlign_Right);
        PortraitSlot->SetVerticalAlignment(VAlign_Bottom);
        Row(Preview, Box(TEXT("ArtHeight"), Art, 0, 230), 12);
        auto* SceneActions = Widget<UHorizontalBox>(TEXT("SceneActions"));
        auto* SceneInfo = Widget<UVerticalBox>(TEXT("SceneInfo"));
        Row(SceneInfo, Text(TEXT("SceneName"), 20), 4);
        Row(SceneInfo, Text(TEXT("SceneStatus"), 16), 0);
        Fill(SceneActions, SceneInfo, 1);
        Auto(SceneActions, Button(TEXT("DefaultScene"), TEXT("DefaultScene")), 12);
        Auto(SceneActions, Button(TEXT("ChangeScene"), TEXT("ChangeScene")));
        Row(Preview, SceneActions, 12);
        Row(Preview, Rule(TEXT("LoadoutTopRule")), 10);
        auto* LoadoutHeaderRow = Widget<UHorizontalBox>(TEXT("LoadoutHeaderRow"));
        auto* LoadoutTitleCol = Widget<UVerticalBox>(TEXT("LoadoutTitleCol"));
        Row(LoadoutTitleCol, Text(TEXT("LoadoutHeading"), 18), 2);
        auto* HintText = Text(TEXT("LoadoutHint"), 14);
        HintText->SetColorAndOpacity(FSlateColor(FLinearColor(0.60f, 0.56f, 0.50f, 1.f)));
        Row(LoadoutTitleCol, HintText, 0);
        Fill(LoadoutHeaderRow, LoadoutTitleCol, 1, 16);
        Auto(LoadoutHeaderRow, ChoiceChip(TEXT("LoadoutCurrent"), 116, 34), 8);
        Auto(LoadoutHeaderRow, ChoiceChip(TEXT("LoadoutRecommended"), 116, 34), 8);
        Auto(LoadoutHeaderRow, ChoiceChip(TEXT("LoadoutCustom"), 116, 34), 0);
        Row(Preview, LoadoutHeaderRow, 10);
        auto* LoadoutStatsRow = Widget<UHorizontalBox>(TEXT("LoadoutStatsRow"));
        Auto(LoadoutStatsRow, Text(TEXT("MaxHealthLabel"), 16), 12);
        Auto(LoadoutStatsRow, StepButton(TEXT("HealthDown"), TEXT("-")), 8);
        auto* HealthVal = Text(TEXT("HealthValue"), 18);
        HealthVal->SetJustification(ETextJustify::Center);
        HealthVal->SetAutoWrapText(false);
        if (auto* ValSlot = Cast<USizeBoxSlot>(Box(TEXT("HealthValBox"), HealthVal, 68, 32)->GetChildAt(0)->Slot))
            ValSlot->SetVerticalAlignment(VAlign_Center);
        Auto(LoadoutStatsRow, CastChecked<UWidget>(HealthVal->GetParent()), 8);
        Auto(LoadoutStatsRow, StepButton(TEXT("HealthUp"), TEXT("+")), 36);
        Auto(LoadoutStatsRow, Text(TEXT("WeaponLevelLabel"), 16), 12);
        Auto(LoadoutStatsRow, StepButton(TEXT("WeaponDown"), TEXT("-")), 8);
        auto* WeaponVal = Text(TEXT("WeaponValue"), 18);
        WeaponVal->SetJustification(ETextJustify::Center);
        WeaponVal->SetAutoWrapText(false);
        if (auto* ValSlot = Cast<USizeBoxSlot>(Box(TEXT("WeaponValBox"), WeaponVal, 56, 32)->GetChildAt(0)->Slot))
            ValSlot->SetVerticalAlignment(VAlign_Center);
        Auto(LoadoutStatsRow, CastChecked<UWidget>(WeaponVal->GetParent()), 8);
        Auto(LoadoutStatsRow, StepButton(TEXT("WeaponUp"), TEXT("+")), 0);
        Row(Preview, LoadoutStatsRow, 0);
        auto* Picker = Widget<UVerticalBox>(TEXT("ScenePicker"));
        Switcher->AddChild(Picker);
        Row(Picker, Text(TEXT("ScenePickerHeading"), 22), 14);
        auto* PickerBody = Widget<UHorizontalBox>(TEXT("PickerBody"));
        auto* PickerBodySlot = Picker->AddChildToVerticalBox(PickerBody);
        PickerBodySlot->SetSize(Stretch);
        auto* SceneScroll = Widget<UScrollBox>(TEXT("SceneScroll"));
        SceneScroll->SetScrollbarThickness(FVector2D(3, 3));
        SceneScroll->SetScrollWhenFocusChanges(EScrollWhenFocusChanges::InstantScroll);
        Auto(PickerBody, Box(TEXT("SceneListWidth"), SceneScroll, 370, 0), 24);
        CastChecked<UHorizontalBoxSlot>(SceneScroll->GetParent()->Slot)->SetVerticalAlignment(VAlign_Fill);
        for (int32 I = 0; I != 64; ++I)
        {
            auto* B = Choice(FString::Printf(TEXT("Scene%d"), I), 58);
            CastChecked<UScrollBoxSlot>(SceneScroll->AddChild(B))->SetPadding(FMargin(0, 0, 0, 8));
        }
        auto* SceneDetail = Widget<UVerticalBox>(TEXT("SceneDetail"));
        Fill(PickerBody, SceneDetail, 1);
        Row(SceneDetail, Box(TEXT("ScenePreviewHeight"), Image(TEXT("ScenePreviewImage")), 0, 235));
        Row(SceneDetail, Text(TEXT("ScenePreviewName"), 22), 8);
        Row(SceneDetail, Text(TEXT("ScenePreviewRegion"), 18), 0);
        auto* PickerActions = Widget<UHorizontalBox>(TEXT("PickerActions"));
        Fill(PickerActions, Widget<USpacer>(TEXT("PickerActionSpacer")), 1);
        Auto(PickerActions, Button(TEXT("CancelScene"), TEXT("CancelScene")), 16);
        Auto(PickerActions, Button(TEXT("UseScene"), TEXT("UseScene"), true));
        Row(Picker, PickerActions, 0);
        Row(Right, Rule(TEXT("SettingsRule")), 12);
        auto* Settings = Widget<UHorizontalBox>(TEXT("Settings"));
        auto* Lock = Button(TEXT("LockHealth"), TEXT("LockHealth"));
        auto* LockLabel = Lock->GetChildAt(0);
        Lock->RemoveChild(LockLabel);
        auto* LockRow = Widget<UHorizontalBox>(TEXT("LockRow"));
        Lock->AddChild(LockRow);
        Auto(LockRow, LockLabel, 18);
        auto* LockTrack = Color(TEXT("LockTrack"), FLinearColor(.15f, .14f, .12f, 1));
        LockTrack->SetPadding(FMargin(4));
        auto* Thumb = Color(TEXT("LockThumb"), FLinearColor(.8f, .77f, .69f, 1));
        auto* ThumbBox = Box(TEXT("LockThumbSize"), Thumb, 18, 18);
        LockTrack->AddChild(ThumbBox);
        LockTrack->SetHorizontalAlignment(HAlign_Left);
        Auto(LockRow, Box(TEXT("LockTrackSize"), LockTrack, 52, 26));
        Auto(Settings, Lock);
        Fill(Settings, Widget<USpacer>(TEXT("SettingsSpacer")), 1);
        Auto(Settings, Text(TEXT("EnergyRule"), 20));
        Row(Right, Settings, 14);
        Row(Right, Rule(TEXT("SettingsBottomRule")), 0);
        Row(Layout, Text(TEXT("Error"), 18), 12);
        auto* Actions = Widget<UHorizontalBox>(TEXT("Actions"));
        Auto(Actions, Button(TEXT("Back"), TEXT("Back")));
        Auto(Actions, Button(TEXT("End"), TEXT("End")));
        Fill(Actions, Widget<USpacer>(TEXT("BeforeCurrentActions")), 1);
        Auto(Actions, Button(TEXT("Reset"), TEXT("Reset")), 16);
        Auto(Actions, Button(TEXT("Continue"), TEXT("Continue")), 16);
        Fill(Actions, Widget<USpacer>(TEXT("BeforeStart")), 1);
        Auto(Actions, Button(TEXT("Start"), TEXT("Start"), true));
        auto* ActionsSlot = Layout->AddChildToVerticalBox(Actions);
        ActionsSlot->SetPadding(FMargin(0, 32, 0, 0));
        Compile();
        BindEvents();
        Compile();
    }

    void Compile()
    {
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        FCompilerResultsLog Results;
        FKismetEditorUtilities::CompileBlueprint(Blueprint, EBlueprintCompileOptions::None, &Results);
        checkf(Results.NumErrors == 0, TEXT("Widget compilation failed"));
    }

    void BindEvents()
    {
        auto* Graph = FBlueprintEditorUtils::FindEventGraph(Blueprint);
        if (!Graph)
        {
            Graph = FBlueprintEditorUtils::CreateNewGraph(Blueprint, TEXT("EventGraph"),
                UEdGraph::StaticClass(), UEdGraphSchema_K2::StaticClass());
            FBlueprintEditorUtils::AddUbergraphPage(Blueprint, Graph);
        }
        for (const auto& Entry : ClickEvents)
        {
            auto* P = FindFProperty<FObjectProperty>(Blueprint->GeneratedClass, Entry.Key);
            auto* Delegate = FindFProperty<FMulticastDelegateProperty>(UButton::StaticClass(), TEXT("OnClicked"));
            check(P && Delegate);
            auto* Event = NewObject<UK2Node_ComponentBoundEvent>(Graph);
            Event->InitializeComponentBoundEventParams(P, Delegate);
            Event->CustomFunctionName = Entry.Value;
            Graph->AddNode(Event, false, false);
            Event->CreateNewGuid();
            Event->AllocateDefaultPins();
            BindSink(Graph, Event);
        }
        // Consume the inherited back action here. Native C++ first cancels a
        // scene draft; it only closes the system menu at the outermost level.
        auto* Back = NewObject<UK2Node_Event>(Graph);
        Back->EventReference.SetExternalMember(TEXT("BackInput"), UPBZSystemWidgetBase::StaticClass());
        Back->bOverrideFunction = true;
        Graph->AddNode(Back, false, false);
        Back->CreateNewGuid(); Back->AllocateDefaultPins();
        BindSink(Graph, Back);
    }

    void BindSink(UEdGraph* Graph, UK2Node_Event* Event)
    {
        // SetRenderOpacity is retained in Shipping (PrintString is stripped).
        // Its receiver is this widget, and opacity remains the normal value.
        auto* Sink = NewObject<UK2Node_CallFunction>(Graph);
        Sink->SetFromFunction(UWidget::StaticClass()->FindFunctionByName(TEXT("SetRenderOpacity")));
        Graph->AddNode(Sink, false, false);
        Sink->CreateNewGuid(); Sink->AllocateDefaultPins();
        Sink->FindPinChecked(TEXT("InOpacity"))->DefaultValue = TEXT("1.0");
        Event->FindPinChecked(UEdGraphSchema_K2::PN_Then)->MakeLinkTo(
            Sink->FindPinChecked(UEdGraphSchema_K2::PN_Execute));
    }
};
}

UArenaAssetsCommandlet::UArenaAssetsCommandlet()
{
    IsClient = false; IsServer = false; IsEditor = true; LogToConsole = true;
}

int32 UArenaAssetsCommandlet::Main(const FString&)
{
    const FString PackageName(TEXT("/Game/PhantomBladeArena/W_Arena"));
    auto* Package = CreatePackage(*PackageName);
    FDesigner Designer(Package);
    Designer.Build();
    FSavePackageArgs Args;
    Args.TopLevelFlags = RF_Public | RF_Standalone;
    const FString Filename = FPackageName::LongPackageNameToFilename(PackageName,
        FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(Package, Designer.Blueprint, *Filename, Args)) return 1;
    UE_LOG(LogTemp, Display, TEXT("Arena asset saved: %s"), *Filename);
    const FString EffectPackageName(TEXT("/Game/PhantomBladeArena/GE_HealthLock"));
    auto* EffectPackage = CreatePackage(*EffectPackageName);
    EffectPackage->FullyLoad();
    auto* EffectBlueprint = FindObject<UBlueprint>(EffectPackage, TEXT("GE_HealthLock"));
    if (!EffectBlueprint) EffectBlueprint = FKismetEditorUtilities::CreateBlueprint(
        UGameplayEffect::StaticClass(), EffectPackage, TEXT("GE_HealthLock"), BPTYPE_Normal,
        UBlueprint::StaticClass(), UBlueprintGeneratedClass::StaticClass());
    FKismetEditorUtilities::CompileBlueprint(EffectBlueprint);
    auto* Effect = CastChecked<UGameplayEffect>(EffectBlueprint->GeneratedClass->GetDefaultObject());
    Effect->DurationPolicy = EGameplayEffectDurationType::Infinite;
    Effect->Modifiers.Reset();
    FGameplayModifierInfo Modifier;
    Modifier.Attribute = FGameplayAttribute(FindFProperty<FProperty>(UBasicAttributeSet::StaticClass(), TEXT("Health")));
    Modifier.ModifierOp = EGameplayModOp::Override;
    FSetByCallerFloat HeldHealth;
    HeldHealth.DataName = TEXT("Arena.Health");
    Modifier.ModifierMagnitude = FGameplayEffectModifierMagnitude(HeldHealth);
    Effect->Modifiers.Add(Modifier);
    EffectBlueprint->MarkPackageDirty();
    const auto EffectFilename = FPackageName::LongPackageNameToFilename(EffectPackageName, FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(EffectPackage, EffectBlueprint, *EffectFilename, Args)) return 1;
    return 0;
}
