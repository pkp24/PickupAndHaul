## major areas to check
1. jobs are synced so a job doesn't get requested and told its fine and then the job is denied. Error: PickUpAndHaul.WorkGiver_HaulToInventory provided target Thing but yielded no actual job for pawn PawnName. The CanGiveJob and JobOnX methods may not be synchronized.
2. caches are properly handled, if there is a chance for desync of the caches or for them to be improperlytaken care of or accessed.
3. null reference exceptions, they can be difficult to track down and can easily occur.
4. infinite loops, theyre very easy to cause with all the multithreadong.
5. New Reservation System Implementation could cause issues, currently pawns get stuck when trying to create jobs.
6. Look at the PUAHForked.log and figure out why the pawn attempting to haul is stuck and won't move.


## normal bug checking
otherwise just do general checks to make sure things weren't forgotten or memory leaks occur.

