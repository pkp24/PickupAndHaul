namespace PickUpAndHaul.Structs;
public class CellAllocation(Thing a, int c)
{
	public Thing Allocated { get; set; } = a;
	public int Capacity { get; set; } = c;
}
