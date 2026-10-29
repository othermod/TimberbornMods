using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.GoodsUI;
using Timberborn.Localization;
using Timberborn.UIFormatters;
using UnityEngine.UIElements;

namespace Spurge;

// rewrites the fruiting-time label in place while a boosted spurge is selected: DESIGN.md#growth-booster
public class SpurgeFragment : IEntityPanelFragment
{
    private static readonly string FruitGoodId = "Badwater";

    private readonly ILoc _loc;

    private readonly GoodDescriber _goodDescriber;

    private readonly Phrase _daysPhrase = Phrase.New().FormatDays<float>("F0");

    private readonly Label _anchor;

    private Label _fruitGrowthTime;

    private SpurgeGrowthBooster _booster;

    public SpurgeFragment(ILoc loc, GoodDescriber goodDescriber)
    {
        _loc = loc;
        _goodDescriber = goodDescriber;
        _anchor = new Label();
        _anchor.ToggleDisplayStyle(false);
    }

    public VisualElement InitializeFragment()
    {
        return _anchor;
    }

    public void ShowFragment(BaseComponent entity)
    {
        _booster = entity.GetComponent<SpurgeGrowthBooster>();
    }

    public void ClearFragment()
    {
        _booster = null;
    }

    public void UpdateFragment()
    {
        Rewrite();
    }

    private void Rewrite()
    {
        if (_booster == null || _anchor.panel == null)
        {
            return;
        }
        float multiplier = _booster.CurrentSpeedMultiplier;
        if (multiplier <= 1f)
        {
            return;
        }
        if (_fruitGrowthTime == null || _fruitGrowthTime.panel == null)
        {
            _fruitGrowthTime = FindFruitGrowthTime();
        }
        if (_fruitGrowthTime != null)
        {
            _fruitGrowthTime.text = _loc.T(_daysPhrase, _booster.BaselineDays() / multiplier);
        }
    }

    private Label FindFruitGrowthTime()
    {
        foreach (Label label in _anchor.panel.visualTree.Query<Label>("GrowthTime").ToList())
        {
            VisualElement parent = label.parent;
            while (parent != null)
            {
                Image icon = parent.Q<Image>("YieldIcon");
                if (icon != null)
                {
                    if (icon.sprite == _goodDescriber.GetIcon(FruitGoodId))
                    {
                        return label;
                    }
                    break;
                }
                parent = parent.parent;
            }
        }
        return null;
    }
}
