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
#include "Components/WrapBox.h"
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

    explicit FDesigner(UPackage* Package, const TCHAR* Name = TEXT("W_Arena"), UClass* Parent = UPBZSystemWidgetBase::StaticClass())
    {
        Package->FullyLoad();
        Blueprint = FindObject<UWidgetBlueprint>(Package, Name);
        if (Blueprint)
        {
            const auto Graphs = Blueprint->UbergraphPages;
            for (const auto& Graph : Graphs) FBlueprintEditorUtils::RemoveGraph(Blueprint, Graph.Get());
            Blueprint->WidgetTree = NewObject<UWidgetTree>(Blueprint, NAME_None, RF_Transactional);
        }
        else
        {
            Blueprint = CastChecked<UWidgetBlueprint>(FKismetEditorUtilities::CreateBlueprint(
                Parent, Package, Name, BPTYPE_Normal,
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

    UButton* Choice(const FString& Name, float Height, bool ShowSubtitle = false)
    {
        auto* B = Button(Name, Name);
        auto* Label = CastChecked<UTextBlock>(B->GetChildAt(0));
        B->RemoveChild(Label);
        Label->SetJustification(ETextJustify::Left);
        Label->SetAutoWrapText(true);
        Label->SetColorAndOpacity(FSlateColor(FLinearColor(0.84f, 0.81f, 0.75f, 1.f)));
        auto Font = Label->GetFont();
        Font.Size = 20;
        Label->SetFont(Font);
        auto* SelectedLabel = Text(Name + TEXT("SelectedLabel"), 20);
        SelectedLabel->SetJustification(ETextJustify::Left);
        SelectedLabel->SetAutoWrapText(true);
        SelectedLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.08f, 0.07f, 0.06f, 1.f)));
        SelectedLabel->SetVisibility(ESlateVisibility::Collapsed);
        auto Style = B->GetStyle();
        Style.NormalPadding = Style.PressedPadding = FMargin(0);
        B->SetStyle(Style);
        auto* Layers = Widget<UOverlay>(Name + TEXT("Layers"));
        auto* RowSize = Box(Name + TEXT("RowSize"), Layers, 0, 0);
        RowSize->SetMinDesiredHeight(Height);
        RowSize->SetClipping(EWidgetClipping::ClipToBounds);
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
        auto* TextColumn = Widget<UVerticalBox>(Name + TEXT("TextColumn"));
        Row(TextColumn, LabelOverlay, ShowSubtitle ? 2 : 0);
        Fill(RowBox, TextColumn, 1);
        CastChecked<UHorizontalBoxSlot>(TextColumn->Slot)->SetVerticalAlignment(VAlign_Center);
        TextColumn->SetClipping(EWidgetClipping::ClipToBounds);
        if (ShowSubtitle)
        {
            auto* SubLabel = Text(Name + TEXT("SubLabel"), 14);
            SubLabel->SetJustification(ETextJustify::Left);
            SubLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.58f, 0.54f, 0.49f, 1.f)));
            auto* SelectedSubLabel = Text(Name + TEXT("SelectedSubLabel"), 14);
            SelectedSubLabel->SetJustification(ETextJustify::Left);
            SelectedSubLabel->SetColorAndOpacity(FSlateColor(FLinearColor(0.22f, 0.19f, 0.16f, 1.f)));
            SelectedSubLabel->SetVisibility(ESlateVisibility::Collapsed);
            auto* SubOverlay = Widget<UOverlay>(Name + TEXT("SubLabelOverlay"));
            auto* UnselSubSlot = SubOverlay->AddChildToOverlay(SubLabel);
            UnselSubSlot->SetHorizontalAlignment(HAlign_Fill);
            UnselSubSlot->SetVerticalAlignment(VAlign_Center);
            auto* SelSubSlot = SubOverlay->AddChildToOverlay(SelectedSubLabel);
            SelSubSlot->SetHorizontalAlignment(HAlign_Fill);
            SelSubSlot->SetVerticalAlignment(VAlign_Center);
            Row(TextColumn, SubOverlay, 0);
        }
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

    UWrapBox* Flow(const FString& Name)
    {
        auto* Result = Widget<UWrapBox>(Name);
        Result->SetInnerSlotPadding(FVector2D(12, 10));
        return Result;
    }

    UWidget* BossControl(const FString& Name)
    {
        auto* Group = Widget<UVerticalBox>(Name + TEXT("Group"));
        Row(Group, Text(Name + TEXT("Label"), 18), 6);
        auto* Controls = Widget<UHorizontalBox>(Name + TEXT("Controls"));
        Auto(Controls, StepButton(Name + TEXT("Down"), TEXT("−")), 8);
        auto* Value = Text(Name + TEXT("Value"), 20);
        Value->SetJustification(ETextJustify::Center);
        Auto(Controls, Box(Name + TEXT("ValueWidth"), Value, 150, 0), 8);
        Auto(Controls, StepButton(Name + TEXT("Up"), TEXT("+")));
        Row(Group, Controls, 0);
        return Box(Name + TEXT("Width"), Group, 260, 0);
    }

    void BuildStats()
    {
        auto* Canvas = Widget<UCanvasPanel>(TEXT("Root"));
        Canvas->SetVisibility(ESlateVisibility::HitTestInvisible);
        Tree->RootWidget = Canvas;
        auto* Backing = Color(TEXT("Backing"), FLinearColor(.04f, .035f, .03f, .78f));
        Backing->SetPadding(FMargin(14, 10));
        Backing->AddChild(Box(TEXT("StatsWidth"), Text(TEXT("Stats"), 17), 310, 0));
        auto* Slot = Canvas->AddChildToCanvas(Backing);
        Slot->SetAnchors(FAnchors(.02f, .22f));
        Slot->SetAutoSize(true);
        Compile();
    }

    void BuildTargetLabel()
    {
        auto* Label = Text(TEXT("Label"), 17);
        Label->SetAutoWrapText(false);
        Label->SetJustification(ETextJustify::Center);
        Label->SetShadowOffset(FVector2D(1, 1));
        Label->SetShadowColorAndOpacity(FLinearColor::Black);
        Tree->RootWidget = Label;
        Compile();
    }

    void Build()
    {
        auto* Canvas = Widget<UCanvasPanel>(TEXT("Root"));
        Tree->RootWidget = Canvas;
        // Fill the native menu's DPI-scaled area instead of shrinking a fixed canvas.
        auto* Layout = Widget<UVerticalBox>(TEXT("Layout"));
        auto* CanvasSlot = Canvas->AddChildToCanvas(Layout);
        CanvasSlot->SetAnchors(FAnchors(.035f, .035f, .965f, .95f));
        CanvasSlot->SetOffsets(FMargin(0));
        Row(Layout, Text(TEXT("CurrentStatus"), 20), 12);
        auto* Main = Widget<UHorizontalBox>(TEXT("Main"));
        auto* MainSlot = Layout->AddChildToVerticalBox(Main);
        FSlateChildSize Stretch; Stretch.SizeRule = ESlateSizeRule::Fill;
        MainSlot->SetSize(Stretch);
        auto* Left = Widget<UVerticalBox>(TEXT("OpponentColumn"));
        Fill(Main, Left, .28f, 32);
        CastChecked<UHorizontalBoxSlot>(Left->Slot)->SetVerticalAlignment(VAlign_Fill);
        Row(Left, Text(TEXT("OpponentHeading"), 26), 20);
        auto* PracticeModes = Flow(TEXT("PracticeModes"));
        for (const FString Name : {TEXT("PracticeBoss"), TEXT("PracticeDummy"), TEXT("PracticeGroup")})
            PracticeModes->AddChildToWrapBox(ChoiceChip(Name, 104, 40));
        Row(Left, PracticeModes, 12);
        Row(Left, Rule(TEXT("OpponentRule")), 20);
        auto* PracticePanel = Widget<UVerticalBox>(TEXT("PracticePanel"));
        Row(PracticePanel, Text(TEXT("PracticeHint"), 17), 12);
        auto* TargetScopes = Flow(TEXT("TargetScopes"));
        TargetScopes->AddChildToWrapBox(ChoiceChip(TEXT("TargetEditCurrent"), 142, 40));
        TargetScopes->AddChildToWrapBox(ChoiceChip(TEXT("TargetEditPending"), 142, 40));
        Row(PracticePanel, TargetScopes, 10);
        Row(PracticePanel, Text(TEXT("TargetCountLabel"), 18), 8);
        auto* TargetPicker = Widget<UHorizontalBox>(TEXT("TargetPicker"));
        Auto(TargetPicker, StepButton(TEXT("TargetPrev"), TEXT("‹")), 8);
        auto* TargetValue = Text(TEXT("TargetValue"), 20);
        TargetValue->SetJustification(ETextJustify::Center);
        Auto(TargetPicker, Box(TEXT("TargetValueWidth"), TargetValue, 150, 0), 8);
        Auto(TargetPicker, StepButton(TEXT("TargetNext"), TEXT("›")));
        Row(PracticePanel, TargetPicker, 8);
        auto* TargetButtons = Flow(TEXT("TargetButtons"));
        TargetButtons->AddChildToWrapBox(Button(TEXT("AddTarget"), TEXT("AddTarget")));
        TargetButtons->AddChildToWrapBox(Button(TEXT("RemoveTarget"), TEXT("RemoveTarget")));
        Row(PracticePanel, TargetButtons, 8);
        Row(PracticePanel, Text(TEXT("TargetStatus"), 17), 8);
        Row(PracticePanel, BossControl(TEXT("TargetKind")), 6);
        Row(PracticePanel, Text(TEXT("TargetEligibility"), 16), 12);
        Row(PracticePanel, BossControl(TEXT("Pose")), 12);
        Row(PracticePanel, BossControl(TEXT("PoseProgress")), 12);
        Row(PracticePanel, BossControl(TEXT("TargetX")), 12);
        Row(PracticePanel, BossControl(TEXT("TargetY")), 12);
        Row(PracticePanel, BossControl(TEXT("TargetYaw")), 12);
        Row(PracticePanel, Text(TEXT("TargetEditHint"), 16), 12);
        Row(PracticePanel, Text(TEXT("TargetStats"), 18), 12);
        Row(PracticePanel, Rule(TEXT("TargetStatsRule")), 12);
        Row(PracticePanel, Text(TEXT("PracticeStats"), 18), 12);
        Row(PracticePanel, Button(TEXT("ClearStats"), TEXT("ClearStats")), 0);
        auto* PracticeScroll = Widget<UScrollBox>(TEXT("PracticeScroll"));
        PracticeScroll->AddChild(PracticePanel);
        auto* PracticeSlot = Left->AddChildToVerticalBox(PracticeScroll);
        PracticeSlot->SetSize(Stretch);
        auto* BossScroll = Widget<UScrollBox>(TEXT("BossScroll"));
        BossScroll->SetScrollbarThickness(FVector2D(3, 3));
        BossScroll->SetScrollbarPadding(FMargin(8, 0));
        BossScroll->SetNavigationDestination(EDescendantScrollDestination::IntoView);
        BossScroll->SetScrollWhenFocusChanges(EScrollWhenFocusChanges::InstantScroll);
        auto* ScrollSlot = Left->AddChildToVerticalBox(BossScroll);
        ScrollSlot->SetSize(Stretch);
        for (int32 I = 0; I != 64; ++I)
        {
            auto* Chapter = Text(FString::Printf(TEXT("BossChapter%d"), I), 15);
            Chapter->SetColorAndOpacity(FSlateColor(FLinearColor(.62f, .58f, .51f, 1)));
            CastChecked<UScrollBoxSlot>(BossScroll->AddChild(Chapter))->SetPadding(FMargin(14, 10, 0, 6));
            auto* B = Choice(FString::Printf(TEXT("Boss%d"), I), 68);
            CastChecked<UScrollBoxSlot>(BossScroll->AddChild(B))->SetPadding(FMargin(0, 0, 0, 8));
        }
        auto* Right = Widget<UVerticalBox>(TEXT("SelectionColumn"));
        Fill(Main, Right, .72f);
        Row(Right, Text(TEXT("PendingStatus"), 18), 8);
        Row(Right, Text(TEXT("OpponentName"), 28), 8);
        auto* Switcher = Widget<UWidgetSwitcher>(TEXT("SceneSwitcher"));
        auto* RegionSlot = Right->AddChildToVerticalBox(Switcher);
        RegionSlot->SetSize(Stretch);
        auto* Preview = Widget<UVerticalBox>(TEXT("Preview"));
        auto* PreviewScroll = Widget<UScrollBox>(TEXT("PreviewScroll"));
        PreviewScroll->SetScrollbarThickness(FVector2D(3, 3));
        PreviewScroll->SetScrollWhenFocusChanges(EScrollWhenFocusChanges::InstantScroll);
        PreviewScroll->SetNavigationDestination(EDescendantScrollDestination::IntoView);
        PreviewScroll->AddChild(Preview);
        Switcher->AddChild(PreviewScroll);
        auto* SceneActions = Widget<UHorizontalBox>(TEXT("SceneActions"));
        Auto(SceneActions, Box(TEXT("ArtHeight"), Image(TEXT("SceneImage"), EStretch::ScaleToFill), 140, 80), 16);
        auto* SceneInfo = Widget<UVerticalBox>(TEXT("SceneInfo"));
        Row(SceneInfo, Text(TEXT("SceneName"), 18), 4);
        Row(SceneInfo, Text(TEXT("SceneStatus"), 15), 0);
        Fill(SceneActions, SceneInfo, 1);
        Auto(SceneActions, Box(TEXT("PortraitFrame"), Image(TEXT("Portrait")), 64, 64), 12);
        Row(Preview, SceneActions, 8);
        auto* SceneButtons = Flow(TEXT("SceneButtons"));
        SceneButtons->AddChildToWrapBox(Button(TEXT("ChangeScene"), TEXT("ChangeScene")));
        SceneButtons->AddChildToWrapBox(Button(TEXT("DefaultScene"), TEXT("DefaultScene")));
        Row(Preview, SceneButtons, 8);
        Row(Preview, Rule(TEXT("LoadoutTopRule")), 8);
        auto* Modes = Flow(TEXT("LoadoutModes"));
        Modes->AddChildToWrapBox(ChoiceChip(TEXT("LoadoutRecommended"), 184, 40));
        Modes->AddChildToWrapBox(ChoiceChip(TEXT("LoadoutCurrent"), 184, 40));
        Modes->AddChildToWrapBox(ChoiceChip(TEXT("LoadoutCustom"), 116, 40));
        Modes->AddChildToWrapBox(Button(TEXT("UseCurrentLoadout"), TEXT("UseCurrentLoadout")));
        Row(Preview, Modes, 6);
        Row(Preview, Text(TEXT("LoadoutHeading"), 20), 4);
        auto* HintText = Text(TEXT("LoadoutHint"), 18);
        HintText->SetColorAndOpacity(FSlateColor(FLinearColor(.68f, .64f, .56f, 1)));
        Row(Preview, HintText, 8);
        for (int32 I = 0; I != 4; ++I)
        {
            const auto Name = FString::Printf(TEXT("Equip%d"), I);
            auto* Header = Widget<UHorizontalBox>(Name + TEXT("Header"));
            Fill(Header, Text(Name + TEXT("Slot"), 18), 1, 12);
            Auto(Header, Text(Name + TEXT("State"), 16));
            Row(Preview, Header, 4);
            auto* EquipRow = Widget<UHorizontalBox>(Name + TEXT("Row"));
            Auto(EquipRow, StepButton(Name + TEXT("Prev"), TEXT("‹")), 6);
            Auto(EquipRow, Box(Name + TEXT("IconBox"), Image(Name + TEXT("Icon")), 32, 32), 8);
            auto* ItemName = Text(Name + TEXT("Name"), 20);
            ItemName->SetClipping(EWidgetClipping::ClipToBounds);
            Fill(EquipRow, ItemName, 1, 6);
            Auto(EquipRow, StepButton(Name + TEXT("Next"), TEXT("›")), 12);
            Auto(EquipRow, StepButton(Name + TEXT("Down"), TEXT("−")), 6);
            auto* Value = Text(Name + TEXT("Level"), 17);
            Value->SetJustification(ETextJustify::Center);
            Auto(EquipRow, Box(Name + TEXT("LevelWidth"), Value, 78, 32), 6);
            Auto(EquipRow, StepButton(Name + TEXT("Up"), TEXT("+")), 12);
            Row(Preview, EquipRow, 14);
        }
        auto* Stats = Widget<UHorizontalBox>(TEXT("LoadoutStatsRow"));
        Auto(Stats, Text(TEXT("MaxHealthLabel"), 16), 12);
        Auto(Stats, StepButton(TEXT("HealthDown"), TEXT("−")), 8);
        auto* HealthVal = Text(TEXT("HealthValue"), 16);
        HealthVal->SetJustification(ETextJustify::Center);
        Auto(Stats, Box(TEXT("HealthValBox"), HealthVal, 170, 32), 8);
        Auto(Stats, StepButton(TEXT("HealthUp"), TEXT("+")), 12);
        auto* HealthControls = Flow(TEXT("HealthControls"));
        HealthControls->AddChildToWrapBox(Stats);
        HealthControls->AddChildToWrapBox(Button(TEXT("HealthAuto"), TEXT("HealthAuto")));
        Row(Preview, HealthControls, 14);
        auto* BossSettings = Widget<UVerticalBox>(TEXT("BossSettings"));
        Row(BossSettings, Rule(TEXT("BossRule")), 12);
        Row(BossSettings, Text(TEXT("BossHeading"), 22), 6);
        Row(BossSettings, Text(TEXT("BossHint"), 18), 10);
        auto* BossControls = Flow(TEXT("BossControls"));
        for (const FString Name : {TEXT("Difficulty"), TEXT("Cycle"), TEXT("BossHealth"), TEXT("BossAttack")})
            BossControls->AddChildToWrapBox(BossControl(Name));
        Row(BossSettings, BossControls, 8);
        auto* BossButtons = Flow(TEXT("BossButtons"));
        BossButtons->AddChildToWrapBox(Button(TEXT("BossDefaults"), TEXT("BossDefaults")));
        Row(BossSettings, BossButtons, 0);
        Row(Preview, BossSettings, 12);
        auto* Picker = Widget<UVerticalBox>(TEXT("ScenePicker"));
        Switcher->AddChild(Picker);
        Row(Picker, Text(TEXT("ScenePickerHeading"), 22), 14);
        auto* PickerBody = Widget<UHorizontalBox>(TEXT("PickerBody"));
        auto* PickerBodySlot = Picker->AddChildToVerticalBox(PickerBody);
        PickerBodySlot->SetSize(Stretch);
        auto* SceneScroll = Widget<UScrollBox>(TEXT("SceneScroll"));
        SceneScroll->SetScrollbarThickness(FVector2D(3, 3));
        SceneScroll->SetScrollWhenFocusChanges(EScrollWhenFocusChanges::InstantScroll);
        Fill(PickerBody, SceneScroll, .4f, 24);
        CastChecked<UHorizontalBoxSlot>(SceneScroll->Slot)->SetVerticalAlignment(VAlign_Fill);
        for (int32 I = 0; I != 64; ++I)
        {
            auto* B = Choice(FString::Printf(TEXT("Scene%d"), I), 58, true);
            CastChecked<UScrollBoxSlot>(SceneScroll->AddChild(B))->SetPadding(FMargin(0, 0, 0, 8));
        }
        auto* SceneDetail = Widget<UVerticalBox>(TEXT("SceneDetail"));
        Fill(PickerBody, SceneDetail, .6f);
        Row(SceneDetail, Box(TEXT("ScenePreviewHeight"), Image(TEXT("ScenePreviewImage")), 0, 235));
        Row(SceneDetail, Text(TEXT("ScenePreviewName"), 22), 8);
        Row(SceneDetail, Text(TEXT("ScenePreviewRegion"), 18), 0);
        auto* PickerActions = Widget<UHorizontalBox>(TEXT("PickerActions"));
        Fill(PickerActions, Widget<USpacer>(TEXT("PickerActionSpacer")), 1);
        Auto(PickerActions, Button(TEXT("CancelScene"), TEXT("CancelScene")), 16);
        Auto(PickerActions, Button(TEXT("UseScene"), TEXT("UseScene"), true));
        Row(Picker, PickerActions, 0);
        Row(Right, Rule(TEXT("SettingsRule")), 12);
        auto* Settings = Flow(TEXT("Settings"));
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
        Settings->AddChildToWrapBox(Lock);
        Settings->AddChildToWrapBox(Text(TEXT("EnergyRule"), 20));
        Row(Right, Settings, 14);
        Row(Right, Rule(TEXT("SettingsBottomRule")), 0);
        Row(Layout, Text(TEXT("Error"), 18), 8);
        auto* Actions = Widget<UHorizontalBox>(TEXT("Actions"));
        Auto(Actions, Button(TEXT("Back"), TEXT("Back")));
        Auto(Actions, Button(TEXT("End"), TEXT("End")));
        auto* PracticeActions = Flow(TEXT("PracticeActions"));
        PracticeActions->SetHorizontalAlignment(HAlign_Right);
        PracticeActions->AddChildToWrapBox(Button(TEXT("Reset"), TEXT("Reset")));
        PracticeActions->AddChildToWrapBox(Button(TEXT("Continue"), TEXT("Continue")));
        PracticeActions->AddChildToWrapBox(Button(TEXT("CopyStage"), TEXT("CopyStage"), true));
        PracticeActions->AddChildToWrapBox(Button(TEXT("Start"), TEXT("Start"), true));
        Fill(Actions, PracticeActions, 1);
        auto* ActionsSlot = Layout->AddChildToVerticalBox(Actions);
        ActionsSlot->SetPadding(FMargin(0, 12, 0, 0));
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
    const FString StatsPackageName(TEXT("/Game/PhantomBladeArena/W_ArenaStats"));
    auto* StatsPackage = CreatePackage(*StatsPackageName);
    FDesigner StatsDesigner(StatsPackage, TEXT("W_ArenaStats"), UUserWidget::StaticClass());
    StatsDesigner.BuildStats();
    const auto StatsFilename = FPackageName::LongPackageNameToFilename(StatsPackageName, FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(StatsPackage, StatsDesigner.Blueprint, *StatsFilename, Args)) return 1;
    const FString LabelPackageName(TEXT("/Game/PhantomBladeArena/W_ArenaTargetLabel"));
    auto* LabelPackage = CreatePackage(*LabelPackageName);
    FDesigner LabelDesigner(LabelPackage, TEXT("W_ArenaTargetLabel"), UUserWidget::StaticClass());
    LabelDesigner.BuildTargetLabel();
    const auto LabelFilename = FPackageName::LongPackageNameToFilename(LabelPackageName, FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(LabelPackage, LabelDesigner.Blueprint, *LabelFilename, Args)) return 1;
    for (const FString EffectName : {TEXT("GE_HealthLock"), TEXT("GE_MaxHealth"), TEXT("GE_BossTuning")})
    {
        const bool Boss = EffectName == TEXT("GE_BossTuning");
        const FString EffectPackageName = TEXT("/Game/PhantomBladeArena/") + EffectName;
        auto* EffectPackage = CreatePackage(*EffectPackageName);
        EffectPackage->FullyLoad();
        auto* EffectBlueprint = FindObject<UBlueprint>(EffectPackage, *EffectName);
        if (!EffectBlueprint) EffectBlueprint = FKismetEditorUtilities::CreateBlueprint(
            UGameplayEffect::StaticClass(), EffectPackage, FName(*EffectName), BPTYPE_Normal,
            UBlueprint::StaticClass(), UBlueprintGeneratedClass::StaticClass());
        FKismetEditorUtilities::CompileBlueprint(EffectBlueprint);
        auto* Effect = CastChecked<UGameplayEffect>(EffectBlueprint->GeneratedClass->GetDefaultObject());
        Effect->DurationPolicy = EGameplayEffectDurationType::Infinite;
        Effect->Modifiers.Reset();
        const TArray<FName> Attributes = Boss ? TArray<FName>{TEXT("Health_Max"), TEXT("Attack"), TEXT("ShachiAttack")}
            : TArray<FName>{EffectName == TEXT("GE_HealthLock") ? TEXT("Health") : TEXT("Health_Max")};
        for (const FName AttributeName : Attributes)
        {
            FGameplayModifierInfo Modifier;
            Modifier.Attribute = FGameplayAttribute(FindFProperty<FProperty>(UBasicAttributeSet::StaticClass(), AttributeName));
            Modifier.ModifierOp = Boss ? EGameplayModOp::MultiplyCompound : EGameplayModOp::Override;
            FSetByCallerFloat Value;
            Value.DataName = Boss ? (AttributeName == TEXT("Health_Max") ? TEXT("Arena.BossHealth") : TEXT("Arena.BossAttack")) : TEXT("Arena.Health");
            Modifier.ModifierMagnitude = FGameplayEffectModifierMagnitude(Value);
            Effect->Modifiers.Add(Modifier);
        }
        EffectBlueprint->MarkPackageDirty();
        const auto EffectFilename = FPackageName::LongPackageNameToFilename(EffectPackageName, FPackageName::GetAssetPackageExtension());
        if (!UPackage::SavePackage(EffectPackage, EffectBlueprint, *EffectFilename, Args)) return 1;
    }
    return 0;
}
