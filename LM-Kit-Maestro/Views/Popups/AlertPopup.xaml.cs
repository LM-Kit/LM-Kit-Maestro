using LMKitMaestro.ViewModels;
using Mopups.Interfaces;

namespace LMKitMaestro.Views;

public partial class AlertPopup : PopupView
{
    public AlertPopup(IPopupNavigation popupNavigation, AlertPopupViewModel informationPopupViewModel) : base(popupNavigation)
    {
        InitializeComponent();
        BindingContext = informationPopupViewModel;
    }
}