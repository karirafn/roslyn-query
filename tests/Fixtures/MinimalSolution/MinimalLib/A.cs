namespace MinimalLib;

public sealed class A
{
    private readonly int _value;

    public A(int value = 0) => _value = value;

    public int M() => _value;
}
